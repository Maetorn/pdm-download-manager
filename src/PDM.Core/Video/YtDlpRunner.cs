using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PDM.Core.Engine;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM.Core.Video;

public sealed class VideoInfo
{
    public string Title { get; init; } = "";
    public double? Duration { get; init; }
    public List<int> Heights { get; init; } = new();
    public bool HasAudio { get; init; }
    public bool IsPlaylist { get; init; }
    public int PlaylistCount { get; init; }
    public string? Extractor { get; init; }
    /// <summary>Approximate size per height (best video of that height + best audio), when known.</summary>
    public Dictionary<int, long> ApproxSizes { get; init; } = new();
}

/// <summary>Runs yt-dlp for one video job. Arguments are passed as a list (no shell), config files are ignored.</summary>
public sealed partial class YtDlpRunner
{
    private readonly ToolManager _tools;
    private readonly string _url;
    private readonly string _folder;
    private readonly VideoOptions _options;
    private readonly string? _referrer;
    private long _downloaded;
    private long _total = -1;
    private long _base;          // bytes of earlier files in the same job (video + audio)
    private long _lastFileBytes;
    private double? _speed;

    public YtDlpRunner(ToolManager tools, string url, string folder, VideoOptions options, string? referrer)
    {
        _tools = tools;
        _url = url;
        _folder = folder;
        _options = options;
        _referrer = referrer;
    }

    public long Downloaded => DirectDownloaded ?? Interlocked.Read(ref _downloaded);
    public long TotalBytes => DirectTotal ?? Interlocked.Read(ref _total);
    /// <summary>yt-dlp's own speed figure; null while PDM's engine downloads (the manager measures it then).</summary>
    public double? ReportedSpeed => IsDirect ? null : _speed;
    public string? Stage { get; private set; }
    public string? OutputPath { get; private set; }
    public Action<string>? FileNameChanged { get; set; }

    /// <summary>Browser cookies for sites that need a login (Instagram, Facebook, …). Given to yt-dlp as a cookies.txt
    /// in the job's temp folder (under %LOCALAPPDATA%\PDM) only while it runs, then deleted.</summary>
    public List<CookieData>? BrowserCookies { get; set; }

    /// <summary>Speed limit for yt-dlp's own downloader, bytes/s (0 = none). PDM's engine uses Limiters instead.</summary>
    public long RateLimitBytes { get; set; }

    private string CookieFile => Path.Combine(TempDir, "cookies.txt");

    /// <summary>Whether this attempt gives yt-dlp the browser cookies (see <see cref="CookiesFirst"/>).</summary>
    private bool _withCookies;

    /// <summary>
    /// Whether to try a site with the browser cookies first. Not on YouTube: signed-in requests make yt-dlp use
    /// player clients that need a JavaScript runtime, and without one every format is dropped ("Requested format
    /// is not available"). There the cookies are only used when the plain attempt fails (age limit, members only).
    /// </summary>
    public static bool CookiesFirst(string url) => !IsYouTube(url);

    /// <summary>The app's display language and English, e.g. "de,en".</summary>
    public static string DefaultSubtitleLangs()
    {
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return lang is "en" or "iv" || lang.Length != 2 ? "en" : lang + ",en";
    }

    public static bool IsYouTube(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var h = u.Host.ToLowerInvariant();
        return h is "youtube.com" or "youtu.be" or "youtube-nocookie.com" or "www.youtube-nocookie.com" || h.EndsWith(".youtube.com", StringComparison.Ordinal);
    }

    /// <summary>A yt-dlp failure that the other cookie choice may fix (not a stop, DRM, missing tool or disk error).</summary>
    private static bool WorthOtherCookieChoice(DownloadOutcome o) =>
        !o.Completed && !o.Cancelled && o.Error == DownloadErrorKind.Unknown
        && o.Detail is { } d && d.Contains("ERROR", StringComparison.Ordinal) && d != "drm" && !d.Contains("ytdlp-missing", StringComparison.Ordinal);

    private bool WriteCookieFile()
    {
        if (BrowserCookies is not { Count: > 0 } list) return false;
        File.WriteAllText(CookieFile, Cookies.ToNetscape(list, DateTimeOffset.UtcNow.AddDays(1)), new UTF8Encoding(false));
        return true;
    }

    private void DeleteCookieFile()
    {
        try { if (File.Exists(CookieFile)) File.Delete(CookieFile); } catch { }
    }

    /// <summary>Temp folder for .part files, so a job can be resumed or cleaned up.</summary>
    public static string TempDirFor(string url) =>
        Path.Combine(AppPaths.DataRoot, "vtemp", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16]);

    /// <summary>Temp folder of this job: the same video in two qualities must not share .part files.</summary>
    private string TempDir => JobTempDir(_url, _options);

    /// <summary>Temp folder of a video job (parts, resume state); deleted when the job finishes or is removed.</summary>
    public static string JobTempDir(string url, VideoOptions o) =>
        TempDirFor(url + "|" + o.Format + "|" + o.AudioOnly + "|" + o.AudioFormat + "|" + o.Container + "|" + o.Playlist);

    /// <summary>yt-dlp appends the final path here (UTF-8). Unlike --print, --print-to-file keeps progress output on.</summary>
    private string ResultFile => Path.Combine(TempDir, "pdm-result.txt");

    /// <summary>" 1080p" for video (yt-dlp templates cannot contain parentheses here) so different qualities of one video get different files; nothing for audio.</summary>
    private string QualitySuffix => _options.AudioOnly ? "" : "%(height& {}p|)s";

    private string SingleTemplate => "%(title).150B" + QualitySuffix + ".%(ext)s";

    public List<string> BuildArguments()
    {
        var a = new List<string>
        {
            "--ignore-config", "--no-colors", "--newline", "--no-mtime", "--windows-filenames",
            "--encoding", "utf-8",
            "--progress", "--progress-template", "download:PDMPROG %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.speed)s",
            "--print-to-file", "after_move:%(filepath)s", ResultFile,
            "--paths", "temp:" + TempDir,
            "--paths", "home:" + _folder,
        };
        if (_tools.HasFfmpeg) { a.Add("--ffmpeg-location"); a.Add(_tools.Dir); }
        AddJsRuntime(a, _tools);
        if (!string.IsNullOrEmpty(_referrer) && FileNames.IsHttpUrl(_referrer, out _)) { a.Add("--referer"); a.Add(_referrer!); }
        if (_withCookies && BrowserCookies is { Count: > 0 }) { a.Add("--cookies"); a.Add(CookieFile); }
        if (RateLimitBytes > 0) { a.Add("--limit-rate"); a.Add(RateLimitBytes.ToString(CultureInfo.InvariantCulture)); }

        if (_options.Playlist)
        {
            a.Add("--yes-playlist");
            a.Add("-o"); a.Add("%(playlist_title).80B/%(playlist_index)03d - %(title).120B" + QualitySuffix + ".%(ext)s");
        }
        else
        {
            a.Add("--no-playlist");
            a.Add("-o"); a.Add(SingleTemplate);
        }

        if (_options.AudioOnly)
        {
            // AAC first: converting it to m4a needs no re-encode.
            a.Add("-f"); a.Add("ba[acodec^=mp4a]/ba/b");
            if (_tools.HasFfmpeg)
            {
                a.Add("-x");
                a.Add("--audio-format"); a.Add(_options.AudioFormat is "mp3" or "m4a" or "opus" ? _options.AudioFormat : "m4a");
            }
        }
        else
        {
            a.Add("-f"); a.Add(string.IsNullOrWhiteSpace(_options.Format) ? "bv*+ba/b" : _options.Format);
            if (_tools.HasFfmpeg && _options.Container is "mp4" or "mkv" or "webm")
            {
                a.Add("--merge-output-format"); a.Add(_options.Container);
            }
        }

        if (_options.Subtitles && _tools.HasFfmpeg)
        {
            a.Add("--write-subs");
            a.Add("--sub-langs"); a.Add(string.IsNullOrWhiteSpace(_options.SubtitleLangs) ? DefaultSubtitleLangs() : _options.SubtitleLangs);
            if (!_options.AudioOnly) a.Add("--embed-subs");
        }
        a.Add("--");
        a.Add(_url);
        return a;
    }

    /// <summary>
    /// yt-dlp format selector. For mp4 the result must play in Windows' own players: H.264 + AAC is preferred up to
    /// 1080p (sites only offer VP9/AV1 above that), and AV1 is avoided whenever anything else exists, because Windows
    /// needs an extra Store extension for AV1. mkv/webm just take the best streams.
    /// </summary>
    public static string FormatFor(int? maxHeight, string? container = "mp4")
    {
        var h = maxHeight is > 0 ? $"[height<={maxHeight}]" : "";
        if (container is "mkv" or "webm")
            return h.Length > 0 ? $"bv*{h}+ba/b{h}/bv*+ba/b" : "bv*+ba/b";
        const string h264 = "[vcodec^=avc]";
        const string aac = "[acodec^=mp4a]";
        const string notAv1 = "[vcodec!^=av01]";
        var parts = new List<string>();
        if (maxHeight is > 0 and <= 1080)
        {
            parts.Add($"bv*{h}{h264}+ba{aac}");
            parts.Add($"bv*{h}{h264}+ba");
        }
        parts.Add($"bv*{h}{notAv1}+ba{aac}");
        parts.Add($"bv*{h}{notAv1}+ba");
        parts.Add($"bv*{h}+ba");
        if (h.Length > 0) parts.Add($"b{h}");
        parts.Add("bv*+ba/b");
        return string.Join("/", parts);
    }

    public async Task<DownloadOutcome> RunAsync(CancellationToken ct)
    {
        try { return await RunCoreAsync(ct).ConfigureAwait(false); }
        finally { DeleteCookieFile(); }
    }

    private async Task<DownloadOutcome> RunCoreAsync(CancellationToken ct)
    {
        if (!_tools.HasYtDlp) return DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, "ytdlp-missing");
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(TempDir);
        var hasCookies = WriteCookieFile();
        _withCookies = hasCookies && CookiesFirst(_url);
        var outcome = await RunAttemptAsync(ct).ConfigureAwait(false);
        if (hasCookies && WorthOtherCookieChoice(outcome))
        {
            _withCookies = !_withCookies;
            Log.Info("Video: trying again " + (_withCookies ? "with" : "without") + " the browser cookies");
            ResetDirect();
            _base = _lastFileBytes = 0;
            Interlocked.Exchange(ref _downloaded, 0);
            Interlocked.Exchange(ref _total, 0);
            outcome = await RunAttemptAsync(ct).ConfigureAwait(false);
        }
        return outcome;
    }

    private async Task<DownloadOutcome> RunAttemptAsync(CancellationToken ct)
    {
        // First choice: yt-dlp only finds the stream addresses, PDM's multi-connection engine downloads them and
        // FFmpeg joins them. Anything unexpected falls back to yt-dlp's own downloader below.
        if (Engine != null && !_options.AudioOnly && !_options.Playlist && !_options.Subtitles)
        {
            var direct = await TryDirectAsync(ct).ConfigureAwait(false);
            if (direct != null) return direct;
            ResetDirect();
            Log.Info("Video: using yt-dlp's own downloader");
        }

        try { File.Delete(ResultFile); } catch { }
        var started = DateTime.UtcNow;

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
        foreach (var arg in BuildArguments()) psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        ApplyNetwork(psi);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lastError = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            OnLine(e.Data); // progress goes to stderr when yt-dlp is quiet
            if (e.Data.StartsWith("ERROR", StringComparison.Ordinal)) { lastError.Clear(); lastError.Append(e.Data); }
            else if (e.Data.StartsWith("WARNING", StringComparison.Ordinal) && lastError.Length == 0) { }
        };

        Stage = "start";
        proc.Start();
        using var job = ToolJob.Contain(proc, singleProcess: false); // yt-dlp starts FFmpeg/Deno itself; all end with PDM
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            return DownloadOutcome.Stopped;
        }

        if (proc.ExitCode == 0)
        {
            ReadResult(started);
        }
        if (proc.ExitCode == 0 && OutputPath != null)
        {
            try { Directory.Delete(TempDir, recursive: true); } catch { }
            if (_total <= 0 && File.Exists(OutputPath)) Interlocked.Exchange(ref _total, new FileInfo(OutputPath).Length);
            return DownloadOutcome.Done;
        }
        var err = lastError.ToString();
        if (err.Contains("DRM", StringComparison.OrdinalIgnoreCase)) return DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, "drm");
        return DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, string.IsNullOrEmpty(err) ? $"yt-dlp exit {proc.ExitCode}" : Trim(err));
    }

    /// <summary>Final path from the result file; if yt-dlp wrote none, the newest new media file in the folder.</summary>
    private void ReadResult(DateTime startedUtc)
    {
        try
        {
            if (File.Exists(ResultFile))
            {
                var last = File.ReadAllLines(ResultFile, Encoding.UTF8).LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                if (last != null && File.Exists(last)) OutputPath = last;
            }
            if (OutputPath == null && Directory.Exists(_folder))
            {
                OutputPath = new DirectoryInfo(_folder).EnumerateFiles()
                    .Where(f => f.LastWriteTimeUtc >= startedUtc.AddSeconds(-5) && f.Extension is not (".part" or ".ytdl" or ".tmp") && !f.Name.Contains(".part-"))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
        }
        catch (Exception ex) { Log.Warn("yt-dlp result: " + ex.GetType().Name); }
        if (OutputPath != null) FileNameChanged?.Invoke(Path.GetFileName(OutputPath));
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private void OnLine(string line)
    {
        if (line.StartsWith("PDMPROG ", StringComparison.Ordinal))
        {
            var p = line.Split(' ');
            if (p.Length < 5) return;
            var done = ParseLong(p[1]);
            var total = ParseLong(p[2]);
            if (total <= 0) total = ParseLong(p[3]);
            var speed = ParseDouble(p[4]);
            if (done < _lastFileBytes / 2 && _lastFileBytes > 0)
            {
                // A new file started (e.g. audio after video): keep earlier bytes in the total.
                _base += _lastFileBytes;
            }
            _lastFileBytes = Math.Max(0, done);
            Interlocked.Exchange(ref _downloaded, _base + Math.Max(0, done));
            if (total > 0) Interlocked.Exchange(ref _total, _base + total);
            _speed = speed > 0 ? speed : null;
            Stage = "download";
        }
        else if (line.StartsWith("PDMFILE ", StringComparison.Ordinal))
        {
            OutputPath = line[8..].Trim();
            FileNameChanged?.Invoke(Path.GetFileName(OutputPath));
        }
        else if (line.StartsWith("[Merger]", StringComparison.Ordinal) || line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal))
        {
            Stage = "merge";
            _speed = null;
        }
        else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal) || line.StartsWith("[VideoConvertor]", StringComparison.Ordinal))
        {
            Stage = "convert";
            _speed = null;
        }
        else if (line.StartsWith("[download] Destination:", StringComparison.Ordinal))
        {
            var name = Path.GetFileName(line["[download] Destination:".Length..].Trim());
            if (!string.IsNullOrEmpty(name)) FileNameChanged?.Invoke(name);
        }
    }

    private static long ParseLong(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : -1;

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : -1;

    /// <summary>Asks yt-dlp for the title and available qualities without downloading.
    /// Cookies are tried first or only after a failure, as for downloads (<see cref="CookiesFirst"/>).</summary>
    public static async Task<VideoInfo> GetInfoAsync(ToolManager tools, string url, bool playlist, CancellationToken ct, List<CookieData>? cookies = null)
    {
        if (!tools.HasYtDlp) throw new InvalidOperationException("ytdlp-missing");
        string? cookieFile = null;
        if (cookies is { Count: > 0 })
        {
            var dir = Path.Combine(AppPaths.DataRoot, "vtemp");
            Directory.CreateDirectory(dir);
            cookieFile = Path.Combine(dir, "info-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(cookieFile, Cookies.ToNetscape(cookies, DateTimeOffset.UtcNow.AddDays(1)), new UTF8Encoding(false));
        }
        try
        {
            var first = cookieFile != null && CookiesFirst(url) ? cookieFile : null;
            try
            {
                return await GetInfoCoreAsync(tools, url, playlist, first, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (cookieFile != null && ex is not OperationCanceledException && !ex.Message.Contains("DRM", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("Video info: trying again " + (first == null ? "with" : "without") + " the browser cookies");
                return await GetInfoCoreAsync(tools, url, playlist, first == null ? cookieFile : null, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (cookieFile != null) try { File.Delete(cookieFile); } catch { }
        }
    }

    /// <summary>Deno for YouTube's JavaScript challenges (all formats, signed-in requests); yt-dlp runs it sandboxed.</summary>
    internal static void AddJsRuntime(IList<string> args, ToolManager tools)
    {
        if (!tools.HasDeno) return;
        args.Add("--js-runtimes");
        args.Add("deno:" + tools.DenoPath);
    }

    /// <summary>PDM's proxy setting for yt-dlp: the custom proxy through the environment (its login stays off the
    /// command line), or "--proxy ''" for a direct connection. Placed first, before the "--" that ends the options.</summary>
    internal static void ApplyNetwork(ProcessStartInfo psi)
    {
        foreach (var (k, v) in PDM.Core.Engine.NetConfig.ToolEnvironment()) psi.Environment[k] = v;
        if (PDM.Core.Engine.NetConfig.ToolProxyArgument() is { } proxy)
        {
            psi.ArgumentList.Insert(0, proxy);
            psi.ArgumentList.Insert(0, "--proxy");
        }
    }

    private static async Task<VideoInfo> GetInfoCoreAsync(ToolManager tools, string url, bool playlist, string? cookieFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--ignore-config", "--no-colors", "-J", playlist ? "--flat-playlist" : "--no-playlist" })
            psi.ArgumentList.Add(a);
        AddJsRuntime(psi.ArgumentList, tools);
        if (cookieFile != null) { psi.ArgumentList.Add("--cookies"); psi.ArgumentList.Add(cookieFile); }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        ApplyNetwork(psi);
        using var p = Process.Start(psi)!;
        using var job = ToolJob.Contain(p, singleProcess: false);
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        }
        catch
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        var json = await stdoutTask.ConfigureAwait(false);
        if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            var err = (await stderrTask.ConfigureAwait(false)).Split('\n').LastOrDefault(l => l.StartsWith("ERROR")) ?? "yt-dlp failed";
            throw new InvalidOperationException(Trim(err.Trim()));
        }
        return ParseInfo(json);
    }

    public static VideoInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        var isPlaylist = root.TryGetProperty("_type", out var ty) && ty.GetString() == "playlist";
        var count = isPlaylist && root.TryGetProperty("entries", out var en) && en.ValueKind == JsonValueKind.Array ? en.GetArrayLength() : 0;
        double? duration = root.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : null;
        var heights = new SortedSet<int>();
        var sizes = new Dictionary<int, long>();
        long bestAudio = 0;
        var hasAudio = false;
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = f.TryGetProperty("vcodec", out var vc) ? vc.GetString() : null;
                var acodec = f.TryGetProperty("acodec", out var ac) ? ac.GetString() : null;
                long size = 0;
                if (f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number) size = fs.GetInt64();
                else if (f.TryGetProperty("filesize_approx", out var fa) && fa.ValueKind == JsonValueKind.Number) size = (long)fa.GetDouble();
                if (acodec is not null and not "none") hasAudio = true;
                if (vcodec is null or "none")
                {
                    if (acodec is not null and not "none") bestAudio = Math.Max(bestAudio, size);
                    continue;
                }
                if (f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                {
                    var height = h.GetInt32();
                    if (height <= 0) continue;
                    heights.Add(height);
                    if (size > 0) sizes[height] = Math.Max(sizes.GetValueOrDefault(height), size);
                }
            }
        }
        foreach (var k in sizes.Keys.ToList()) sizes[k] += bestAudio;
        return new VideoInfo
        {
            Title = title,
            Duration = duration,
            Heights = heights.Reverse().ToList(),
            HasAudio = hasAudio,
            IsPlaylist = isPlaylist,
            PlaylistCount = count,
            Extractor = root.TryGetProperty("extractor_key", out var ek) ? ek.GetString() : null,
            ApproxSizes = sizes,
        };
    }
}
