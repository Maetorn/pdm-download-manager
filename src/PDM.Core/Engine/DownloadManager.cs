using System.Collections.ObjectModel;
using System.Security.Cryptography;
using PDM.Core.Models;
using PDM.Core.Security;
using PDM.Core.Video;

namespace PDM.Core.Engine;

/// <summary>
/// Owns the download list and runs jobs. All public members must be called on the owner thread
/// (the UI thread); engine callbacks are marshalled back through the SynchronizationContext.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private sealed class Running
    {
        public required DownloadItem Item;
        public required CancellationTokenSource Cts;
        public HttpDownloader? Http;
        public YtDlpRunner? Video;
        public Task? Task;
        public long LastBytes;
        public DateTime LastTick = DateTime.UtcNow;
        public double Speed;
        public bool RestartRequested;
        /// <summary>This download's own limit (the global one applies as well).</summary>
        public readonly SpeedLimiter Limiter = new();
    }

    private readonly SynchronizationContext? _ctx;
    private readonly object _inlineGate = new();
    private readonly Dictionary<Guid, Running> _running = new();
    private readonly Timer _tickTimer;
    private readonly Timer _saveTimer;
    private bool _dirty;
    private long _queueCounter;
    private bool _disposed;

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public AppSettings Settings { get; private set; }
    public SpeedLimiter GlobalLimiter { get; } = new();
    public ToolManager Tools { get; }

    /// <summary>Raised on the owner thread.</summary>
    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;
    /// <summary>The last item of the main queue finished (used for the post-queue action).</summary>
    /// <summary>Finished downloads and totals (also after they leave the list).</summary>
    public History History { get; } = new();

    /// <summary>A queue has nothing left to download (queue id).</summary>
    public event Action<string>? QueueFinished;

    public double TotalSpeed { get; private set; }
    public int ActiveCount => _running.Count;

    /// <summary>Test hook: engine options factory.</summary>
    public Func<EngineOptions>? EngineOptionsOverride { get; set; }

    public DownloadManager(AppSettings settings, SynchronizationContext? context, ToolManager? tools = null)
    {
        Settings = settings;
        _ctx = context;
        Tools = tools ?? new ToolManager(AppPaths.ToolsDir);
        ApplySettings(settings);
        // Idle (nothing running, no retry waiting) the timer does not wake the UI thread at all.
        _tickTimer = new Timer(_ => { if (_running.Count > 0 || _retriesPending || TotalSpeed > 0) Post(Tick); }, null, 500, 500);
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        // Checkpoints flush the partial files to disk first, so keep them a few seconds apart.
        _saveTimer = new Timer(_ => SaveCheckpoint(), null, 5000, 5000);
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        GlobalLimiter.BytesPerSecond = settings.SpeedLimitKBps * 1024L;
        Pump();
    }

    // ---------------- loading / saving ----------------

    public void Load()
    {
        foreach (var item in Store.LoadDownloads())
        {
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting)
                item.Status = DownloadStatus.Queued; // resume what was running when the app closed
            else if (item.Status == DownloadStatus.Processing)
                item.Status = DownloadStatus.Error;
            if (item.Status != DownloadStatus.Completed && item.Segments.Count > 0)
                item.DownloadedBytes = item.Segments.Sum(s => Math.Max(0, Math.Min(s.Position, s.End + 1) - s.Start));
            _queueCounter = Math.Max(_queueCounter, item.QueueOrder);
            Items.Add(item);
        }
        Pump();
    }

    public void SaveNow()
    {
        foreach (var r in _running.Values)
            if (r.Http != null) r.Item.Segments = r.Http.Checkpoint();
        try { Store.SaveDownloads(Items.ToList()); _dirty = false; }
        catch (Exception ex) { Log.Error("Saving list failed", ex); }
    }

    private void SaveCheckpoint()
    {
        // Runs on a timer thread: take flushed snapshots here, then apply + save on the owner thread.
        if (_running.Count == 0 && !_dirty) return;
        List<(DownloadItem, List<SegmentState>)> snaps;
        lock (_running) snaps = _running.Values.Where(r => r.Http != null).Select(r => (r.Item, r.Http!.Checkpoint())).ToList();
        if (snaps.Count == 0 && !_dirty) return;
        Post(() =>
        {
            foreach (var (item, segs) in snaps)
                if (segs.Count > 0) item.Segments = segs;
            try { Store.SaveDownloads(Items.ToList()); _dirty = false; }
            catch (Exception ex) { Log.Error("Saving list failed", ex); }
        });
    }

    private void MarkDirty() => _dirty = true;

    // ---------------- commands ----------------

    public DownloadItem Add(AddRequest req)
    {
        if (req.Kind == DownloadKind.Http && !FileNames.IsHttpUrl(req.Url, out _))
            throw new ArgumentException("Only http and https links can be downloaded.");

        var name = string.IsNullOrWhiteSpace(req.FileName) ? null : FileNames.Sanitize(req.FileName);
        var item = new DownloadItem
        {
            Kind = req.Kind,
            Url = req.Url.Trim(),
            Referrer = req.Referrer,
            PageUrl = req.PageUrl,
            UserAgent = req.UserAgent,
            FileName = name ?? FileNames.Sanitize(FileNames.FromUrl(req.Url)),
            AutoName = name == null,
            Folder = string.IsNullOrWhiteSpace(req.Folder) ? "" : req.Folder!,
            TotalBytes = req.SizeHint,
            ContentType = req.ContentType,
            Connections = req.Connections ?? Settings.DefaultConnections,
            ExpectedSha256 = NormalizeHash(req.ExpectedSha256),
            Video = req.Video,
            QueueOrder = ++_queueCounter,
            QueueId = req.StartNow ? null : Settings.FindQueue(req.QueueId)?.Id ?? QueueInfo.MainId,
            Status = req.StartNow ? DownloadStatus.Queued : DownloadStatus.Paused,
        };
        if (req.Cookies is { Count: > 0 } && Settings.AcceptBrowserCookies)
            item.ProtectedCookies = Dpapi.Protect(Cookies.Serialize(req.Cookies));

        Items.Insert(0, item);
        MarkDirty();
        Log.Info($"Added {item.Kind} {FileNames.Redact(item.Url)}");
        if (req.StartNow) StartNow(item);
        SaveNow();
        return item;
    }

    /// <summary>A file PDM made from a download (e.g. converted to MP3/MP4): listed as finished, marked like its source.</summary>
    public DownloadItem AddFinishedFile(string path, DownloadItem source)
    {
        var info = new FileInfo(path);
        if (OperatingSystem.IsWindows()) AttachmentScanner.WriteZoneIdentifier(path, source.FinalUrl ?? source.Url, source.Referrer);
        var item = new DownloadItem
        {
            Kind = DownloadKind.Http,
            Url = source.Url,
            FinalUrl = source.FinalUrl,
            Referrer = source.Referrer,
            PageUrl = source.PageUrl,
            FileName = info.Name,
            Folder = info.DirectoryName!,
            TotalBytes = info.Length,
            DownloadedBytes = info.Length,
            Status = DownloadStatus.Completed,
            CompletedAt = DateTime.Now,
            QueueOrder = ++_queueCounter,
        };
        Items.Insert(0, item);
        MarkDirty();
        SaveNow();
        return item;
    }

    private static string? NormalizeHash(string? h)
    {
        if (string.IsNullOrWhiteSpace(h)) return null;
        h = h.Trim().Replace(" ", "").ToLowerInvariant();
        return h.Length == 64 && h.All(Uri.IsHexDigit) ? h : null;
    }

    /// <summary>Starts immediately, even when the concurrent limit is reached.</summary>
    public void StartNow(DownloadItem item)
    {
        if (_running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return;
        item.RetryAt = null;
        Launch(item);
    }

    /// <summary>Puts the item in the waiting line (starts when a slot is free).</summary>
    public void Resume(DownloadItem item)
    {
        if (_running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return;
        item.Status = DownloadStatus.Queued;
        item.Error = null;
        item.RetryAt = null;
        MarkDirty();
        Pump();
    }

    public void Pause(DownloadItem item)
    {
        if (_running.TryGetValue(item.Id, out var r)) r.Cts.Cancel();
        else if (item.Status is DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        item.RetryAt = null; // the user stopped it: no automatic retry
        MarkDirty();
    }

    /// <summary>Speed limit for one download, KB/s (0 = none). Takes effect immediately.</summary>
    public void SetSpeedLimit(DownloadItem item, int kbps)
    {
        item.SpeedLimitKBps = kbps;
        if (_running.TryGetValue(item.Id, out var r)) r.Limiter.BytesPerSecond = item.SpeedLimitKBps * 1024L;
        MarkDirty();
    }

    /// <summary>An item for the same address (ignoring #fragment and a trailing slash), if any.</summary>
    public DownloadItem? FindDuplicate(string url)
    {
        static string Norm(string? u)
        {
            if (string.IsNullOrEmpty(u)) return "";
            var i = u.IndexOf('#');
            if (i >= 0) u = u[..i];
            return u.Trim().TrimEnd('/');
        }
        var n = Norm(url);
        if (n.Length == 0) return null;
        return Items.FirstOrDefault(i => string.Equals(Norm(i.Url), n, StringComparison.Ordinal) || string.Equals(Norm(i.FinalUrl), n, StringComparison.Ordinal));
    }

    // ---------------- automatic retry ----------------

    private int _retryTick;
    /// <summary>Some failed download waits for its automatic retry (keeps the tick timer running).</summary>
    private volatile bool _retriesPending;

    /// <summary>Errors that usually go away by themselves: lost connection, server busy or briefly broken.</summary>
    public static bool IsTransientError(string? code) =>
        code is "network" or "http:408" or "http:429" or "http:500" or "http:502" or "http:503" or "http:504";

    private static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(30),
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10),
    };

    private void CheckRetries()
    {
        _retriesPending = Items.Any(i => i.RetryAt != null);
        if (!Settings.AutoRetry) return;
        var now = DateTime.Now;
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Error && i.RetryAt is { } at && at <= now).ToList())
        {
            item.RetryAt = null;
            Log.Info($"Auto retry {item.AutoRetries}: {item.FileName}");
            Resume(item);
        }
    }

    /// <summary>The network came back: retry waiting downloads now instead of at their scheduled time.</summary>
    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        Post(() =>
        {
            foreach (var item in Items.Where(i => i.Status == DownloadStatus.Error && i.RetryAt != null)) item.RetryAt = DateTime.Now;
            _retriesPending = true;
        });
    }

    public void PauseAll()
    {
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Queued)) item.Status = DownloadStatus.Paused;
        foreach (var r in _running.Values.ToList()) r.Cts.Cancel();
        MarkDirty();
    }

    public void StartQueue(string queueId = QueueInfo.MainId)
    {
        foreach (var item in Items.Where(i => i.QueueId == queueId && i.Status is DownloadStatus.Paused or DownloadStatus.Error))
        {
            item.Status = DownloadStatus.Queued;
            item.Error = null;
        }
        MarkDirty();
        Pump();
    }

    public void StopQueue(string queueId = QueueInfo.MainId)
    {
        foreach (var item in Items.Where(i => i.QueueId == queueId))
        {
            if (_running.TryGetValue(item.Id, out var r)) r.Cts.Cancel();
            else if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        }
        MarkDirty();
    }

    /// <summary>Moves items into a queue (null = out of every queue). Finished items are left alone.</summary>
    public void SetQueue(IEnumerable<DownloadItem> items, string? queueId)
    {
        if (queueId != null && Settings.FindQueue(queueId) == null) return;
        foreach (var item in items)
        {
            if (item.Status == DownloadStatus.Completed) continue;
            if (queueId != null && item.QueueId == null) item.QueueOrder = ++_queueCounter; // joins at the end
            item.QueueId = queueId;
        }
        MarkDirty();
        SaveNow();
    }

    /// <summary>A queue was deleted: its items wait in the main queue.</summary>
    public void QueueRemoved(string queueId)
    {
        foreach (var item in Items.Where(i => i.QueueId == queueId)) item.QueueId = QueueInfo.MainId;
        MarkDirty();
    }

    /// <summary>Discards progress and downloads again from zero.</summary>
    public void Restart(DownloadItem item)
    {
        if (_running.TryGetValue(item.Id, out var r))
        {
            r.RestartRequested = true;
            r.Cts.Cancel();
            return;
        }
        ResetProgress(item);
        StartNow(item);
    }

    private void ResetProgress(DownloadItem item)
    {
        TryDelete(item.TempPath);
        DeleteVideoTemp(item);
        item.Segments = new();
        item.DownloadedBytes = 0;
        item.SupportsResume = false;
        item.ETag = null;
        item.LastModified = null;
        item.FinalUrl = null;
        item.Error = null;
        item.CompletedAt = null;
        item.Sha256 = null;
        if (item.Status == DownloadStatus.Completed) item.Status = DownloadStatus.Paused;
        MarkDirty();
    }

    /// <summary>"Refresh download address": keep progress but use a new (e.g. freshly signed) link.</summary>
    public void RefreshUrl(DownloadItem item, string newUrl, string? referrer = null, List<CookieData>? cookies = null)
    {
        if (!FileNames.IsHttpUrl(newUrl, out _)) throw new ArgumentException("Invalid link.");
        var wasRunning = _running.TryGetValue(item.Id, out var r);
        if (wasRunning) r!.Cts.Cancel();
        item.Url = newUrl.Trim();
        item.FinalUrl = null;
        if (referrer != null) item.Referrer = referrer;
        if (cookies is { Count: > 0 } && Settings.AcceptBrowserCookies) item.ProtectedCookies = Dpapi.Protect(Cookies.Serialize(cookies));
        item.Error = null;
        MarkDirty();
        SaveNow();
        if (!wasRunning && item.Status != DownloadStatus.Completed) Resume(item);
    }

    public void SetConnections(DownloadItem item, int connections)
    {
        item.Connections = connections;
        if (_running.TryGetValue(item.Id, out var r))
        {
            if (r.Http != null) r.Http.TargetConnections = connections;
            r.Video?.SetConnections(connections);
        }
        MarkDirty();
    }

    /// <summary>Removes from the list. Partial files are always deleted; finished files only when asked (to the Recycle Bin).</summary>
    public void Remove(DownloadItem item, bool deleteFile)
    {
        if (_running.TryGetValue(item.Id, out var r))
        {
            r.Cts.Cancel();
            lock (_running) _running.Remove(item.Id);
            var temp = item.TempPath;
            r.Task?.ContinueWith(_ => { TryDelete(temp); DeleteVideoTemp(item); }, TaskScheduler.Default);
        }
        else
        {
            TryDelete(item.TempPath);
            DeleteVideoTemp(item);
        }
        if (deleteFile && item.Status == DownloadStatus.Completed && File.Exists(item.FullPath))
            RecycleBin.Send(item.FullPath);
        Items.Remove(item);
        MarkDirty();
        SaveNow();
        Pump();
    }

    public void ClearCompleted()
    {
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Completed).ToList()) Items.Remove(item);
        MarkDirty();
        SaveNow();
    }

    /// <summary>Leftovers of a video job (parts, resume state, yt-dlp .part files).</summary>
    private static void DeleteVideoTemp(DownloadItem item)
    {
        if (item.Kind != DownloadKind.Video) return;
        try
        {
            var dir = YtDlpRunner.JobTempDir(item.Url, item.Video ?? new VideoOptions());
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- scheduling ----------------

    private void Pump()
    {
        if (_disposed) return;
        var slots = Settings.MaxConcurrentDownloads - _running.Count;
        if (slots <= 0) return;
        // A queue may allow fewer downloads at once than the general limit (e.g. one after another).
        var perQueue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in _running.Values)
            if (r.Item.QueueId is { } q) perQueue[q] = perQueue.GetValueOrDefault(q) + 1;
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Queued && !_running.ContainsKey(i.Id)).OrderBy(i => i.QueueOrder).ToList())
        {
            if (slots <= 0) break;
            if (item.QueueId is { } q && Settings.FindQueue(q)?.MaxConcurrent is > 0 and var max)
            {
                if (perQueue.GetValueOrDefault(q) >= max) continue;
                perQueue[q] = perQueue.GetValueOrDefault(q) + 1;
            }
            Launch(item);
            slots--;
        }
    }

    private void Launch(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.Folder)) item.Folder = Settings.DownloadFolder;
        item.Status = DownloadStatus.Connecting;
        item.Error = null;
        item.StatusDetail = null;
        var run = new Running { Item = item, Cts = new CancellationTokenSource(), LastBytes = item.DownloadedBytes };
        run.Limiter.BytesPerSecond = item.SpeedLimitKBps * 1024L;
        item.RetryAt = null;
        lock (_running) _running[item.Id] = run;

        if (item.Kind == DownloadKind.Video)
        {
            // Category subfolder for video jobs: Video, or Music for audio only (only for the default folder).
            if (Settings.UseCategoryFolders && string.Equals(item.Folder.TrimEnd('\\', '/'), Settings.DownloadFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                item.Folder = Path.Combine(Settings.DownloadFolder, FileCategories.FolderName(item.Video?.AudioOnly == true ? FileCategory.Music : FileCategory.Video));
            run.Video = new YtDlpRunner(Tools, item.Url, item.Folder, item.Video ?? new VideoOptions(), item.Referrer)
            {
                // Streams are fetched by our own multi-connection engine when possible.
                Engine = MakeOptions(item),
                Limiters = [GlobalLimiter, run.Limiter],
                RateLimitBytes = EffectiveLimit(item),
                BrowserCookies = Cookies.Deserialize(Dpapi.Unprotect(item.ProtectedCookies)),
            };
            run.Video.FileNameChanged = n => Post(() => { item.FileName = n; });
            run.Task = RunVideoAsync(run);
        }
        else
        {
            run.Task = RunHttpAsync(run);
        }
        MarkDirty();
    }

    /// <summary>Strictest of the global and the item limit, bytes/s (0 = none); for yt-dlp's own downloader.</summary>
    private long EffectiveLimit(DownloadItem item)
    {
        var limits = new[] { Settings.SpeedLimitKBps, item.SpeedLimitKBps }.Where(l => l > 0).ToList();
        return limits.Count == 0 ? 0 : limits.Min() * 1024L;
    }

    private EngineOptions MakeOptions(DownloadItem item) => EngineOptionsOverride?.Invoke() ?? new EngineOptions
    {
        Connections = item.Connections,
        MaxRetries = Settings.MaxRetries,
        StallTimeout = TimeSpan.FromSeconds(Settings.TimeoutSeconds),
    };

    private async Task RunHttpAsync(Running run)
    {
        var item = run.Item;
        var cookies = Cookies.Build(Cookies.Deserialize(Dpapi.Unprotect(item.ProtectedCookies)));
        Uri.TryCreate(item.Url, UriKind.Absolute, out var url);
        Uri.TryCreate(item.FinalUrl, UriKind.Absolute, out var finalUrl);
        var hasProgress = item.Segments.Count > 0 && File.Exists(item.TempPath);
        var job = new DownloadJob
        {
            Url = url!,
            FinalUrl = finalUrl,
            Referrer = item.Referrer,
            UserAgent = !string.IsNullOrWhiteSpace(Settings.CustomUserAgent) ? Settings.CustomUserAgent : item.UserAgent ?? EngineOptions.DefaultUserAgent,
            Cookies = cookies,
            TempPath = hasProgress ? item.TempPath : null,
            TotalBytes = item.TotalBytes,
            SupportsRanges = item.SupportsResume,
            ETag = item.ETag,
            LastModified = item.LastModified,
            Segments = hasProgress ? item.Segments.Select(s => new SegmentState { Start = s.Start, End = s.End, Position = s.Position }).ToList() : new(),
        };

        // Values read here so the engine thread never touches the item.
        var autoName = item.AutoName;
        var fileName = item.FileName;
        var folder = item.Folder;
        var explicitFolder = !string.Equals(folder, Settings.DownloadFolder, StringComparison.OrdinalIgnoreCase);
        var takenPaths = Items.Where(i => i != item && i.Status != DownloadStatus.Completed).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldTemp = item.TempPath;

        var dl = new HttpDownloader(job, MakeOptions(item), GlobalLimiter, run.Limiter);
        run.Http = dl;
        dl.Status = s => Post(() => item.StatusDetail = s);
        dl.Probed = info => Post(() =>
        {
            item.TotalBytes = info.TotalBytes;
            item.SupportsResume = info.SupportsRanges && info.StatusCode == 206;
            item.ContentType = info.ContentType;
            item.ETag = info.ETag;
            item.LastModified = info.LastModified;
            item.FinalUrl = info.FinalUrl.AbsoluteUri;
            item.Status = DownloadStatus.Downloading;
            MarkDirty();
        });
        dl.ResolveTempPath = info =>
        {
            // Fresh start: settle on the final name (server name if we only had a guess) and make it unique.
            var name = fileName;
            if (autoName)
            {
                var fromServer = info.FileName ?? FileNames.FromUrl(info.FinalUrl.AbsoluteUri);
                if (!string.IsNullOrWhiteSpace(fromServer)) name = fromServer!;
            }
            name = FileNames.EnsureExtension(FileNames.Sanitize(name), info.ContentType);
            var dir = folder;
            if (Settings.UseCategoryFolders && !explicitFolder)
            {
                var sub = FileCategories.FolderName(FileCategories.Of(name));
                if (sub.Length > 0) dir = Path.Combine(folder, sub);
            }
            Directory.CreateDirectory(dir);
            // Refuse before writing anything when the file cannot fit (sparse preallocation would only fail later).
            if (!DiskSpace.HasRoomFor(dir, info.TotalBytes)) throw DiskSpace.NotEnoughSpace();
            if (!hasProgress) TryDelete(oldTemp);
            name = FileNames.MakeUnique(dir, name, p => takenPaths.Contains(p));
            var full = FileNames.CombineInside(dir, name);
            Post(() =>
            {
                item.Folder = dir;
                item.FileName = name;
                item.AutoName = false;
                MarkDirty();
            });
            return full + ".pdmdl";
        };

        DownloadOutcome outcome;
        try
        {
            outcome = await Task.Run(() => dl.RunAsync(run.Cts.Token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Engine crashed", ex);
            outcome = DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, ex.Message);
        }

        var segments = dl.SnapshotSegments();
        var tempPath = job.TempPath;
        ScanResult? scan = null;
        string? finalPath = null;
        string? hash = null;
        if (outcome.Completed && tempPath != null)
        {
            Post(() => { item.Status = DownloadStatus.Processing; item.DownloadedBytes = dl.TotalBytes; });
            try
            {
                finalPath = tempPath.EndsWith(".pdmdl", StringComparison.OrdinalIgnoreCase) ? tempPath[..^6] : tempPath + ".done";
                if (File.Exists(finalPath))
                {
                    var dir = Path.GetDirectoryName(finalPath)!;
                    finalPath = Path.Combine(dir, FileNames.MakeUnique(dir, Path.GetFileName(finalPath)));
                }
                File.Move(tempPath, finalPath);
                if (Settings.ScanWithAttachmentServices)
                    scan = AttachmentScanner.Scan(finalPath, item.FinalUrl ?? item.Url, item.Referrer);
                else
                    AttachmentScanner.WriteZoneIdentifier(finalPath, item.FinalUrl ?? item.Url, item.Referrer);
                if (item.ExpectedSha256 != null && scan != ScanResult.Blocked)
                    hash = await ComputeSha256Async(finalPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Finishing download failed", ex);
                outcome = DownloadOutcome.Fail(DownloadErrorKind.FileSystem, 0, ex.Message);
            }
        }

        Post(() => Finished(run, outcome, segments, finalPath, scan, hash, dl.TotalBytes));
    }

    private async Task RunVideoAsync(Running run)
    {
        DownloadOutcome outcome;
        try
        {
            Post(() => run.Item.Status = DownloadStatus.Downloading);
            outcome = await Task.Run(() => run.Video!.RunAsync(run.Cts.Token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Video job crashed", ex);
            outcome = DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, ex.Message);
        }
        string? finalPath = null;
        ScanResult? scan = null;
        if (outcome.Completed && run.Video!.OutputPath is { } p && File.Exists(p))
        {
            finalPath = p;
            if (Settings.ScanWithAttachmentServices) scan = AttachmentScanner.Scan(p, run.Item.Url, run.Item.Referrer);
            else AttachmentScanner.WriteZoneIdentifier(p, run.Item.Url, run.Item.Referrer);
        }
        var total = run.Video!.TotalBytes;
        Post(() => Finished(run, outcome, null, finalPath, scan, null, total));
    }

    private void Finished(Running run, DownloadOutcome outcome, List<SegmentState>? segments, string? finalPath, ScanResult? scan, string? hash, long total)
    {
        var item = run.Item;
        lock (_running) _running.Remove(item.Id);
        run.Cts.Dispose();
        if (!Items.Contains(item)) { Pump(); return; } // removed meanwhile

        if (segments is { Count: > 0 }) item.Segments = segments;
        if (_shuttingDown && outcome.Cancelled)
        {
            item.Status = DownloadStatus.Queued; // continue on next start
            return;
        }
        item.Speed = 0;
        item.Eta = null;
        item.ActiveConnections = 0;
        item.StatusDetail = null;
        var queueId = item.QueueId;

        if (outcome.Completed)
        {
            if (finalPath != null)
            {
                item.Folder = Path.GetDirectoryName(finalPath)!;
                item.FileName = Path.GetFileName(finalPath);
            }
            if (total > 0) item.TotalBytes = total;
            item.DownloadedBytes = item.TotalBytes > 0 ? item.TotalBytes : item.DownloadedBytes;
            item.ProtectedCookies = null; // not needed any more
            item.Segments = new();
            item.CompletedAt = DateTime.Now;
            if (hash != null) item.Sha256 = hash;

            item.AutoRetries = 0;
            if (scan == ScanResult.Blocked)
            {
                item.Status = DownloadStatus.Error;
                item.Error = "blocked";
                Log.Warn($"Blocked by antivirus/policy: {item.FileName}");
                Failed?.Invoke(item);
            }
            else if (item.ExpectedSha256 != null && hash != null && !hash.Equals(item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                item.Status = DownloadStatus.Error;
                item.Error = "hash";
                Failed?.Invoke(item);
            }
            else
            {
                item.Status = DownloadStatus.Completed;
                item.InQueue = false;
                Log.Info($"Completed {item.FileName}");
                History.Record(item);
                Completed?.Invoke(item);
            }
        }
        else if (outcome.Cancelled)
        {
            if (run.RestartRequested)
            {
                ResetProgress(item);
                item.Status = DownloadStatus.Queued;
                Launch(item);
            }
            else
            {
                item.Status = DownloadStatus.Paused;
            }
        }
        else
        {
            if (outcome.Error is DownloadErrorKind.ContentChanged or DownloadErrorKind.RangeUnsupported)
            {
                // Saved progress is useless now.
                TryDelete(item.TempPath);
                item.Segments = new();
                item.DownloadedBytes = 0;
                item.SupportsResume = false;
            }
            item.Status = DownloadStatus.Error;
            item.Error = ErrorCode(outcome);
            Log.Warn($"Failed {FileNames.Redact(item.Url)}: {FileNames.RedactUrlsIn(item.Error)}"); // tool errors may quote full addresses
            if (Settings.AutoRetry && IsTransientError(item.Error) && item.AutoRetries < 10)
            {
                item.RetryAt = DateTime.Now + RetryDelay(item.AutoRetries);
                _retriesPending = true;
                item.AutoRetries++;
            }
            Failed?.Invoke(item);
        }

        MarkDirty();
        SaveNow();
        Pump();

        if (queueId != null && !Items.Any(i => i.QueueId == queueId && (i.IsActive || i.Status == DownloadStatus.Queued)))
            QueueFinished?.Invoke(queueId);
    }

    /// <summary>Machine-readable error code; the UI translates it.</summary>
    private static string ErrorCode(DownloadOutcome o) => o.Error switch
    {
        DownloadErrorKind.HttpStatus => $"http:{o.HttpStatus}",
        DownloadErrorKind.DiskFull => "diskfull",
        DownloadErrorKind.ContentChanged => "changed",
        DownloadErrorKind.RangeUnsupported => "norange",
        DownloadErrorKind.Network => "network",
        DownloadErrorKind.FileSystem => "fs",
        _ => "unknown:" + (o.Detail ?? ""),
    };

    // ---------------- progress ----------------

    private void Tick()
    {
        if (_disposed) return;
        if (++_retryTick % 4 == 0) CheckRetries();
        double total = 0;
        var now = DateTime.UtcNow;
        foreach (var r in _running.Values)
        {
            long bytes;
            long size;
            int active;
            if (r.Http != null)
            {
                bytes = r.Http.Downloaded;
                size = r.Http.TotalBytes;
                active = r.Http.ActiveWorkers;
            }
            else if (r.Video != null)
            {
                bytes = r.Video.Downloaded;
                size = r.Video.TotalBytes;
                active = r.Video.ActiveConnections;
                r.Item.StatusDetail = r.Video.Stage;
            }
            else continue;

            var dt = (now - r.LastTick).TotalSeconds;
            if (dt > 0.2)
            {
                var inst = Math.Max(0, bytes - r.LastBytes) / dt;
                r.Speed = r.Speed <= 0 ? inst : r.Speed * 0.7 + inst * 0.3;
                if (r.Video?.ReportedSpeed is { } vs) r.Speed = vs;
                r.LastBytes = bytes;
                r.LastTick = now;
            }
            var item = r.Item;
            if (size > 0) item.TotalBytes = size;
            item.DownloadedBytes = bytes;
            item.ActiveConnections = active;
            item.Speed = r.Speed;
            item.Eta = r.Speed > 1 && item.TotalBytes > 0 ? TimeSpan.FromSeconds(Math.Max(0, item.TotalBytes - bytes) / r.Speed) : null;
            if (item.Status == DownloadStatus.Connecting && bytes > 0) item.Status = DownloadStatus.Downloading;
            total += r.Speed;
        }
        TotalSpeed = total;
    }

    /// <summary>Live segment layout for the details view.</summary>
    public List<SegmentState>? LiveSegments(DownloadItem item) =>
        !_running.TryGetValue(item.Id, out var r) ? null : r.Http != null ? r.Http.SnapshotSegments() : r.Video?.LiveSegments();

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct, IProgress<double>? progress = null)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0;
        var len = fs.Length;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            done += n;
            progress?.Report(len > 0 ? done * 100.0 / len : 100);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private void Post(Action a)
    {
        if (_ctx != null) _ctx.Post(_ => { try { a(); } catch (Exception ex) { Log.Error("UI callback failed", ex); } }, null);
        else lock (_inlineGate) a();
    }

    /// <summary>Pauses everything and saves; call before exit.</summary>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        List<Running> runs;
        lock (_running) runs = _running.Values.ToList();
        foreach (var r in runs) r.Cts.Cancel();
        try { await Task.WhenAll(runs.Select(r => r.Task ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true); } catch { }
        foreach (var r in runs)
        {
            if (r.Http != null)
            {
                var segs = r.Http.SnapshotSegments();
                if (segs.Count > 0) r.Item.Segments = segs;
            }
            if (r.Item.IsActive) r.Item.Status = DownloadStatus.Queued;
        }
        SaveNow();
        History.Flush();
    }

    private bool _shuttingDown;

    public void Dispose()
    {
        _disposed = true;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _tickTimer.Dispose();
        _saveTimer.Dispose();
    }
}
