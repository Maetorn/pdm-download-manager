using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PDM.Core.Engine;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM.Core.Video;

/// <summary>One stream of a video (the picture or the sound) that can be fetched with plain HTTP range requests.</summary>
public sealed record DirectStream(string Url, string Ext, long Size, string? UserAgent, string? Referer, long ChunkSize, bool HasVideo, bool HasAudio, string? FormatId = null);

/// <summary>Progress of the multi-connection path, saved next to the parts so a paused or interrupted job continues.</summary>
public sealed class DirectState
{
    public List<DirectPartState> Parts { get; set; } = new();
}

public sealed class DirectPartState
{
    /// <summary>format id + extension + position: a different quality never resumes into this part.</summary>
    public string Key { get; set; } = "";
    public long Total { get; set; } = -1;
    public bool Done { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<SegmentState> Segments { get; set; } = new();
}

/// <summary>What yt-dlp chose for the requested quality: one or two direct streams and the final file name.</summary>
public sealed record DirectPlan(List<DirectStream> Streams, string FileName);

/// <summary>
/// Multi-connection path for video jobs: yt-dlp only resolves the chosen formats (-J), PDM's HttpDownloader fetches
/// each stream with several connections (like IDM), and FFmpeg joins picture and sound without re-encoding.
/// Returns null whenever this is not possible, and the caller uses yt-dlp's own downloader instead.
/// </summary>
public sealed partial class YtDlpRunner
{
    private readonly object _directLock = new();
    private DirectPlan? _plan;
    private HttpDownloader? _engine;
    private int _engineIndex;
    private long _directDone;
    private long[] _partSizes = [];

    /// <summary>Engine settings (connections, retries, timeouts). Null = always use yt-dlp's own downloader.</summary>
    public EngineOptions? Engine { get; set; }

    /// <summary>Speed limiters shared with normal downloads.</summary>
    public SpeedLimiter[] Limiters { get; set; } = [];

    private bool IsDirect { get { lock (_directLock) return _plan != null; } }

    private long? DirectDownloaded
    {
        get
        {
            lock (_directLock) return _plan == null ? null : _directDone + (_engine?.Downloaded ?? 0);
        }
    }

    private long? DirectTotal
    {
        get
        {
            lock (_directLock)
            {
                if (_plan == null) return null;
                long sum = 0;
                for (var i = 0; i < _plan.Streams.Count; i++)
                {
                    var size = i < _engineIndex ? _partSizes[i]
                        : i == _engineIndex && _engine is { TotalBytes: > 0 } e ? e.TotalBytes
                        : _plan.Streams[i].Size;
                    if (size <= 0) return -1;
                    sum += size;
                }
                return sum;
            }
        }
    }

    /// <summary>Connections in use (1 for yt-dlp's own downloader).</summary>
    public int ActiveConnections { get { lock (_directLock) return _engine?.ActiveWorkers ?? 1; } }

    public void SetConnections(int connections)
    {
        lock (_directLock)
        {
            if (Engine != null) Engine.Connections = Math.Clamp(connections, 1, 16);
            if (_engine != null) _engine.TargetConnections = connections;
        }
    }

    /// <summary>Segment map across the whole job: finished streams first, then the one downloading now.</summary>
    public List<SegmentState>? LiveSegments()
    {
        HttpDownloader? engine;
        long done;
        lock (_directLock)
        {
            engine = _engine;
            done = _directDone;
        }
        if (engine == null) return null;
        var list = new List<SegmentState>();
        if (done > 0) list.Add(new SegmentState { Start = 0, End = done - 1, Position = done });
        foreach (var s in engine.SnapshotSegments())
            list.Add(new SegmentState { Start = s.Start + done, End = s.End + done, Position = s.Position + done });
        return list;
    }

    private void ResetDirect()
    {
        lock (_directLock)
        {
            _plan = null;
            _engine = null;
            _engineIndex = 0;
            _directDone = 0;
        }
    }

    private async Task<DownloadOutcome?> TryDirectAsync(CancellationToken ct)
    {
        Stage = "start";
        string json;
        try
        {
            json = await DumpJsonAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DownloadOutcome.Stopped;
        }
        catch (Exception ex)
        {
            Log.Warn("Video: resolving formats failed: " + ex.GetType().Name);
            return null;
        }

        DirectPlan? plan;
        try { plan = ParseDirectPlan(json, _options.Container); }
        catch (Exception ex)
        {
            Log.Warn("Video: unreadable format info: " + ex.GetType().Name);
            return null;
        }
        if (plan == null || (plan.Streams.Count > 1 && !_tools.HasFfmpeg)) return null;

        lock (_directLock)
        {
            _plan = plan;
            _partSizes = new long[plan.Streams.Count];
        }
        FileNameChanged?.Invoke(plan.FileName);

        // Parts go to the temp folder, the result to the target folder: both need room.
        var expected = plan.Streams.Sum(s => Math.Max(0, s.Size));
        if (!DiskSpace.HasRoomFor(TempDir, expected) || !DiskSpace.HasRoomFor(_folder, expected))
            return DownloadOutcome.Fail(DownloadErrorKind.DiskFull);

        var state = LoadState();
        var parts = new List<string>();
        for (var i = 0; i < plan.Streams.Count; i++)
        {
            var stream = plan.Streams[i];
            if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri)) return null;
            var part = Path.Combine(TempDir, $"part{i}.{SafeExt(stream.Ext)}");
            var key = $"{stream.FormatId}|{stream.Ext}|{i}";
            var saved = state.Parts.FirstOrDefault(p => p.Key == key);

            // Finished in an earlier run (e.g. the picture, before a pause during the sound). Only trusted while the new
            // answer of yt-dlp still describes a stream of the same size: if the site re-encoded the format under the
            // same id, an old picture must not be joined with a new sound.
            var sameStream = stream.Size <= 0 || saved == null || stream.Size == saved.Total;
            if (saved is { Done: true, Total: > 0 } && sameStream && File.Exists(part) && new FileInfo(part).Length == saved.Total)
            {
                lock (_directLock)
                {
                    _partSizes[i] = saved.Total;
                    _directDone += saved.Total;
                    _engineIndex = i + 1;
                }
                parts.Add(part);
                continue;
            }
            var resume = saved is { Done: false, Total: > 0, Segments.Count: > 0 } && sameStream && File.Exists(part);
            if (!resume)
            {
                TryDelete(part);
                state.Parts.RemoveAll(p => p.Key == key);
                saved = null;
            }
            var job = new DownloadJob
            {
                Url = uri,
                UserAgent = string.IsNullOrWhiteSpace(stream.UserAgent) ? EngineOptions.DefaultUserAgent : stream.UserAgent!,
                Referrer = stream.Referer ?? _referrer,
                Cookies = _withCookies ? PDM.Core.Engine.Cookies.Build(BrowserCookies) : null,
                TempPath = part,
            };
            if (saved != null)
            {
                // Continue where it stopped; the engine checks that the server file is still the same one.
                job.TotalBytes = saved.Total;
                job.SupportsRanges = true;
                job.ETag = saved.ETag;
                job.LastModified = saved.LastModified;
                job.Segments = saved.Segments.Select(s => new SegmentState { Start = s.Start, End = s.End, Position = s.Position }).ToList();
            }
            var settings = Engine!;
            var options = new EngineOptions
            {
                Connections = settings.Connections,
                MaxRetries = settings.MaxRetries,
                StallTimeout = settings.StallTimeout,
                MaxRequestBytes = stream.ChunkSize > 0 ? stream.ChunkSize : 0,
            };
            var engine = new HttpDownloader(job, options, Limiters);
            lock (_directLock)
            {
                _engine = engine;
                _engineIndex = i;
            }
            Stage = "download";
            DownloadOutcome outcome;
            // Saved every few seconds, so even a crash or power cut loses little.
            using (new Timer(_ => SavePart(state, key, job, engine.Checkpoint(), done: false), null, 5000, 5000))
                outcome = await engine.RunAsync(ct).ConfigureAwait(false);
            if (outcome.Cancelled || ct.IsCancellationRequested)
            {
                SavePart(state, key, job, engine.SnapshotSegments(), done: false);
                return DownloadOutcome.Stopped;
            }
            if (!outcome.Completed)
            {
                if (outcome.Error == DownloadErrorKind.DiskFull) return outcome;
                Log.Warn($"Video: direct download of stream {i} failed ({outcome.Error} {outcome.HttpStatus})");
                return null;
            }
            lock (_directLock)
            {
                _partSizes[i] = engine.TotalBytes;
                _directDone += engine.TotalBytes;
                _engine = null;
                _engineIndex = i + 1;
            }
            SavePart(state, key, job, new List<SegmentState>(), done: true);
            parts.Add(part);
        }

        var name = FileNames.MakeUnique(_folder, FileNames.Sanitize(plan.FileName, "video"));
        var target = FileNames.CombineInside(_folder, name);
        try
        {
            if (parts.Count == 1)
            {
                File.Move(parts[0], target);
            }
            else
            {
                Stage = "merge";
                var video = plan.Streams.FindIndex(s => s.HasVideo);
                var audio = plan.Streams.FindIndex(s => s.HasAudio && !s.HasVideo);
                if (video < 0 || audio < 0) return null;
                if (!await MergeAsync(parts[video], parts[audio], target, ct).ConfigureAwait(false)) return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DownloadOutcome.Stopped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Video: saving the file failed", ex);
            return DownloadOutcome.Fail(DownloadErrorKind.FileSystem, 0, ex.Message);
        }

        OutputPath = target;
        FileNameChanged?.Invoke(Path.GetFileName(target));
        try { Directory.Delete(TempDir, recursive: true); } catch { }
        return DownloadOutcome.Done;
    }

    private string StateFile => Path.Combine(TempDir, "pdm-state.json");
    private readonly object _stateLock = new();

    private DirectState LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize(File.ReadAllText(StateFile), PdmJson.Default.DirectState) ?? new DirectState();
        }
        catch (Exception ex) { Log.Warn("Video: unreadable resume state: " + ex.GetType().Name); }
        return new DirectState();
    }

    /// <summary>Records one part's progress (atomic replace, so a crash mid-write keeps the previous state).</summary>
    private void SavePart(DirectState state, string key, DownloadJob job, List<SegmentState> segments, bool done)
    {
        lock (_stateLock)
        {
            try
            {
                if (!done && segments.Count == 0) return;
                // A late periodic save must not reopen a part that is already complete.
                if (!done && state.Parts.Any(p => p.Key == key && p.Done)) return;
                state.Parts.RemoveAll(p => p.Key == key);
                state.Parts.Add(new DirectPartState { Key = key, Total = job.TotalBytes, Done = done, ETag = job.ETag, LastModified = job.LastModified, Segments = segments });
                var tmp = StateFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, PdmJson.Default.DirectState));
                File.Move(tmp, StateFile, overwrite: true);
            }
            catch (Exception ex) { Log.Warn("Video: saving resume state failed: " + ex.GetType().Name); }
        }
    }

    /// <summary>Asks yt-dlp which formats it would download for this quality, without downloading.</summary>
    private async Task<string> DumpJsonAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_tools.YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _folder,
        };
        var a = psi.ArgumentList;
        foreach (var x in new[] { "--ignore-config", "--no-colors", "--no-warnings", "--no-playlist", "--windows-filenames", "--encoding", "utf-8", "-J" }) a.Add(x);
        a.Add("-f"); a.Add(string.IsNullOrWhiteSpace(_options.Format) ? "bv*+ba/b" : _options.Format);
        if (_options.Container is "mp4" or "mkv" or "webm") { a.Add("--merge-output-format"); a.Add(_options.Container); }
        if (_tools.HasFfmpeg) { a.Add("--ffmpeg-location"); a.Add(_tools.Dir); }
        AddJsRuntime(a, _tools);
        if (!string.IsNullOrEmpty(_referrer) && FileNames.IsHttpUrl(_referrer, out _)) { a.Add("--referer"); a.Add(_referrer!); }
        if (_withCookies && File.Exists(CookieFile)) { a.Add("--cookies"); a.Add(CookieFile); }
        a.Add("-o"); a.Add(SingleTemplate);
        a.Add("--");
        a.Add(_url);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        ApplyNetwork(psi);

        using var p = Process.Start(psi)!;
        using var job = ToolJob.Contain(p, singleProcess: false); // yt-dlp may start Deno/FFmpeg; ends with PDM
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }
        catch
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        var json = await stdout.ConfigureAwait(false);
        if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            var err = (await stderr.ConfigureAwait(false)).Split('\n').LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? "exit " + p.ExitCode;
            throw new InvalidOperationException(err.Length > 300 ? err[..300] : err);
        }
        return json;
    }

    /// <summary>
    /// Reads yt-dlp's -J output. Returns null unless every chosen format is a plain http(s) download
    /// (HLS/DASH fragments, playlists and anything unusual stay with yt-dlp).
    /// </summary>
    public static DirectPlan? ParseDirectPlan(string json, string? container)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Str(root, "_type") is { } type && type != "video") return null;
        if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True) return null;

        var formats = new List<JsonElement>();
        if (root.TryGetProperty("requested_formats", out var rf) && rf.ValueKind == JsonValueKind.Array) formats.AddRange(rf.EnumerateArray());
        else formats.Add(root);
        if (formats.Count is 0 or > 2) return null;

        var streams = new List<DirectStream>();
        foreach (var f in formats)
        {
            if (f.ValueKind != JsonValueKind.Object) return null;
            var url = Str(f, "url");
            var protocol = Str(f, "protocol");
            if (url == null || protocol is not ("https" or "http") || !FileNames.IsHttpUrl(url, out _)) return null;
            var ext = Str(f, "ext") ?? "bin";
            long size = Num(f, "filesize") ?? Num(f, "filesize_approx") ?? -1;
            string? ua = null, referer = null;
            if (f.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                ua = Str(h, "User-Agent");
                referer = Str(h, "Referer");
            }
            long chunk = 0;
            if (f.TryGetProperty("downloader_options", out var d) && d.ValueKind == JsonValueKind.Object) chunk = Num(d, "http_chunk_size") ?? 0;
            var vcodec = Str(f, "vcodec");
            var acodec = Str(f, "acodec");
            var hasVideo = vcodec != null ? vcodec != "none" : Num(f, "height") > 0;
            var hasAudio = acodec != null ? acodec != "none" : !hasVideo;
            streams.Add(new DirectStream(url, ext, size, ua, referer is not null && FileNames.IsHttpUrl(referer, out _) ? referer : null,
                Math.Clamp(chunk, 0, 1L << 30), hasVideo, hasAudio, Str(f, "format_id")));
        }
        if (streams.Count == 2 && (!streams.Any(s => s.HasVideo) || !streams.Any(s => s.HasAudio && !s.HasVideo))) return null;

        var name = Str(root, "filename") ?? Str(root, "_filename");
        name = string.IsNullOrWhiteSpace(name) ? (Str(root, "title") ?? "video") + "." + streams[0].Ext : Path.GetFileName(name);
        if (streams.Count == 2 && container is "mp4" or "mkv" or "webm") name = Path.ChangeExtension(name, container);
        return new DirectPlan(streams, name);
    }

    /// <summary>Joins picture and sound into one file without re-encoding.</summary>
    private async Task<bool> MergeAsync(string video, string audio, string target, CancellationToken ct)
    {
        var ext = Path.GetExtension(target).ToLowerInvariant();
        var tmp = Path.Combine(TempDir, "merged" + ext);
        TryDelete(tmp);
        var psi = new ProcessStartInfo(_tools.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = TempDir,
        };
        foreach (var x in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", video, "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy" })
            psi.ArgumentList.Add(x);
        if (ext is ".mp4" or ".m4v" or ".mov") { psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart"); }
        psi.ArgumentList.Add(tmp);

        using var p = Process.Start(psi)!;
        using var job = ToolJob.Contain(p, singleProcess: true); // FFmpeg reads downloaded streams: no other programs
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        if (p.ExitCode != 0 || !File.Exists(tmp))
        {
            var err = (await stderr.ConfigureAwait(false)).Trim();
            Log.Warn("Video: FFmpeg merge failed: " + (err.Length > 200 ? err[..200] : err));
            TryDelete(tmp);
            return false;
        }
        File.Move(tmp, target);
        return true;
    }

    private static string SafeExt(string ext)
    {
        var e = new string(ext.Where(char.IsLetterOrDigit).Take(8).ToArray());
        return e.Length > 0 ? e : "bin";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d >= 0 ? (long)d : null;
}
