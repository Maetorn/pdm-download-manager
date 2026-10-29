using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM.Core.Engine;

public enum DownloadErrorKind
{
    None,
    Network,
    HttpStatus,
    DiskFull,
    ContentChanged,
    RangeUnsupported,
    FileSystem,
    Cancelled,
    Unknown,
}

public sealed record DownloadOutcome(bool Completed, bool Cancelled, DownloadErrorKind Error = DownloadErrorKind.None, int HttpStatus = 0, string? Detail = null)
{
    public static readonly DownloadOutcome Done = new(true, false);
    public static readonly DownloadOutcome Stopped = new(false, true, DownloadErrorKind.Cancelled);
    public static DownloadOutcome Fail(DownloadErrorKind kind, int status = 0, string? detail = null) => new(false, false, kind, status, detail);
}

/// <summary>What the first response told us about the file.</summary>
public sealed class ProbeInfo
{
    public Uri FinalUrl { get; init; } = null!;
    public int StatusCode { get; init; }
    public long TotalBytes { get; init; } = -1;
    public bool SupportsRanges { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
}

public sealed class DownloadJob
{
    public required Uri Url { get; init; }
    public Uri? FinalUrl { get; set; }
    public string? Referrer { get; init; }
    public string UserAgent { get; init; } = EngineOptions.DefaultUserAgent;
    public CookieContainer? Cookies { get; init; }

    // Resume state (empty for a fresh download).
    public string? TempPath { get; set; }
    public long TotalBytes { get; set; } = -1;
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<SegmentState> Segments { get; set; } = new();
}

public sealed class EngineOptions
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    public int Connections { get; set; } = 8;
    public int MaxRetries { get; set; } = 12;
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public long MinSplitBytes { get; set; } = 512 * 1024;
    public int BufferSize { get; set; } = 128 * 1024;
    /// <summary>Test hook: delay multiplier for retries (1 = normal).</summary>
    public double RetryDelayScale { get; set; } = 1.0;
    /// <summary>
    /// Largest byte range asked for in one request (0 = a whole segment at once). Some servers (YouTube) slow down
    /// big ranges, so each connection then walks its segment in pieces of this size, back to back.
    /// </summary>
    public long MaxRequestBytes { get; set; }
}

/// <summary>
/// Downloads one URL with up to N parallel HTTP range requests into a single preallocated file.
/// Segments split dynamically: whenever a connection is free, the largest remaining piece is halved.
/// </summary>
public sealed class HttpDownloader
{
    private sealed class Seg
    {
        public long Start;
        public long End;   // inclusive; may shrink when split
        public long Pos;   // next byte to write
        public bool Active;
        public bool Done => Pos > End;
        public long Remaining => Math.Max(0, End - Pos + 1);
    }

    private sealed class FatalException(DownloadOutcome outcome) : Exception(outcome.Detail)
    {
        public DownloadOutcome Outcome { get; } = outcome;
    }

    private sealed class RetryableException(string message, TimeSpan? retryAfter = null, bool throttled = false) : Exception(message)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public bool Throttled { get; } = throttled;
    }

    private const long UnknownEnd = long.MaxValue - 1;

    private readonly DownloadJob _job;
    private readonly EngineOptions _options;
    private readonly SpeedLimiter[] _limiters;
    private readonly object _lock = new();
    private readonly List<Seg> _segs = new();
    private HttpClient? _client;
    private SafeFileHandle? _file;
    private bool _rangesOk;
    private long _total = -1;
    private Uri _requestUrl = null!;
    private volatile int _targetConnections;
    private int _activeWorkers;
    private DownloadOutcome? _fatal;

    /// <summary>Called once for a fresh download with the probe result; returns the temp file path to write.</summary>
    public Func<ProbeInfo, string>? ResolveTempPath { get; set; }

    /// <summary>Called when the probe result is known (fresh or resumed).</summary>
    public Action<ProbeInfo>? Probed { get; set; }

    /// <summary>Short human-readable status, e.g. retry messages. Raised from worker threads.</summary>
    public Action<string>? Status { get; set; }

    public HttpDownloader(DownloadJob job, EngineOptions options, params SpeedLimiter[] limiters)
    {
        _job = job;
        _options = options;
        _limiters = limiters;
        _targetConnections = Math.Clamp(options.Connections, 1, 16);
    }

    public int TargetConnections
    {
        get => _targetConnections;
        set => _targetConnections = Math.Clamp(value, 1, 16);
    }

    public int ActiveWorkers => Volatile.Read(ref _activeWorkers);
    public long TotalBytes => Interlocked.Read(ref _total);
    public bool SupportsRanges => _rangesOk;

    public long Downloaded
    {
        get
        {
            lock (_lock)
            {
                long sum = 0;
                foreach (var s in _segs) sum += Math.Max(0, Math.Min(s.Pos, s.End + 1) - s.Start);
                return sum;
            }
        }
    }

    public List<SegmentState> SnapshotSegments()
    {
        lock (_lock)
            return _segs.Where(s => s.End != UnknownEnd).Select(s => new SegmentState { Start = s.Start, End = s.End, Position = Math.Min(s.Pos, s.End + 1) }).ToList();
    }

    /// <summary>Snapshot taken before flushing, so saved progress never claims more than is on disk.</summary>
    public List<SegmentState> Checkpoint()
    {
        var snap = SnapshotSegments();
        var f = _file;
        try { if (f != null) NativeFile.Flush(f); } catch { }
        return snap;
    }

    public static HttpClient CreateClient(CookieContainer? cookies)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = cookies != null,
            CookieContainer = cookies ?? new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 32,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ResponseDrainTimeout = TimeSpan.FromSeconds(1),
        };
        NetConfig.Apply(handler);
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private HttpRequestMessage BuildRequest(Uri url, long? from, long? to)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(_job.UserAgent) ? EngineOptions.DefaultUserAgent : _job.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrEmpty(_job.Referrer) && FileNames.IsHttpUrl(_job.Referrer, out _))
            req.Headers.TryAddWithoutValidation("Referer", _job.Referrer);
        if (from.HasValue) req.Headers.Range = new RangeHeaderValue(from, to);
        return req;
    }

    /// <summary>Sends a 1-byte range request to learn name, size and range support without downloading.</summary>
    public static async Task<ProbeInfo> ProbeAsync(DownloadJob job, CancellationToken ct)
    {
        using var client = CreateClient(job.Cookies);
        var d = new HttpDownloader(job, new EngineOptions());
        using var req = d.BuildRequest(job.Url, 0, 0);
        using var resp = await SendForHeadersAsync(client, req, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode >= 400) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        return Describe(resp, job.Url);
    }

    private static ProbeInfo Describe(HttpResponseMessage resp, Uri requested)
    {
        var final = resp.RequestMessage?.RequestUri ?? requested;
        long total = -1;
        var ranges = false;
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange is { } cr)
        {
            ranges = cr.Unit == "bytes";
            total = cr.Length ?? -1;
        }
        else if (resp.StatusCode == HttpStatusCode.OK)
        {
            total = resp.Content.Headers.ContentLength ?? -1;
        }
        var cd = resp.Content.Headers.ContentDisposition?.ToString();
        if (cd == null && resp.Content.Headers.TryGetValues("Content-Disposition", out var raw)) cd = raw.FirstOrDefault();
        var encoded = resp.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase));
        return new ProbeInfo
        {
            FinalUrl = final,
            StatusCode = (int)resp.StatusCode,
            TotalBytes = encoded ? -1 : total,
            SupportsRanges = ranges && total > 0 && !encoded,
            FileName = FileNames.FromContentDisposition(cd),
            ContentType = resp.Content.Headers.ContentType?.MediaType,
            ETag = resp.Headers.ETag?.ToString(),
            LastModified = resp.Content.Headers.LastModified?.ToString("R"),
        };
    }

    public async Task<DownloadOutcome> RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var workers = new List<Task>();
        try
        {
            _client = CreateClient(_job.Cookies);
            _requestUrl = _job.FinalUrl ?? _job.Url;

            HttpResponseMessage? firstResponse = null;
            Seg? firstSeg = null;

            if (!await TryResumeAsync(token).ConfigureAwait(false))
            {
                (firstResponse, firstSeg) = await StartFreshAsync(token).ConfigureAwait(false);
            }

            if (firstSeg != null)
            {
                firstSeg.Active = true;
                workers.Add(RunWorkerAsync(firstSeg, firstResponse, token));
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    if (_fatal != null) break;
                    if (_segs.All(s => s.Done)) break;
                    while (workers.Count(w => !w.IsCompleted) < _targetConnections && TryTakeSegment(out var seg))
                    {
                        seg!.Active = true;
                        workers.Add(RunWorkerAsync(seg, null, token));
                    }
                }
                workers.RemoveAll(w => w.IsCompleted);
                if (workers.Count == 0)
                {
                    lock (_lock)
                    {
                        if (_fatal != null || _segs.All(s => s.Done)) break;
                    }
                    // Nothing running but work left (e.g. all workers backed off and exited): loop picks it up.
                    await Task.Delay(50, token).ConfigureAwait(false);
                    continue;
                }
                await Task.WhenAny(Task.WhenAny(workers), Task.Delay(250, token)).ConfigureAwait(false);
            }

            if (_fatal != null)
            {
                cts.Cancel();
                await WaitQuietly(workers).ConfigureAwait(false);
                return _fatal;
            }

            await WaitQuietly(workers).ConfigureAwait(false);
            return Finish();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            return DownloadOutcome.Stopped;
        }
        catch (FatalException fx)
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            return fx.Outcome;
        }
        catch (Exception ex) when (NativeFile.IsDiskFull(ex))
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            return DownloadOutcome.Fail(DownloadErrorKind.DiskFull);
        }
        catch (HttpRequestException ex)
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            return DownloadOutcome.Fail(ex.StatusCode.HasValue ? DownloadErrorKind.HttpStatus : DownloadErrorKind.Network, (int)(ex.StatusCode ?? 0), ex.Message);
        }
        catch (RetryableException rx)
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            return DownloadOutcome.Fail(DownloadErrorKind.Network, 0, rx.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            return DownloadOutcome.Fail(DownloadErrorKind.FileSystem, 0, ex.Message);
        }
        catch (Exception ex)
        {
            cts.Cancel();
            await WaitQuietly(workers).ConfigureAwait(false);
            Log.Error("Download failed", ex);
            return DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, ex.Message);
        }
        finally
        {
            try { if (_file != null) NativeFile.Flush(_file); } catch { }
            _file?.Dispose();
            _file = null;
            _client?.Dispose();
            _client = null;
        }
    }

    private static async Task WaitQuietly(List<Task> tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
    }

    private DownloadOutcome Finish()
    {
        if (_file == null) return DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, "no file");
        lock (_lock)
        {
            if (_total < 0)
            {
                // Unknown size: total is what we received.
                var s = _segs[0];
                _total = s.Pos;
            }
        }
        NativeFile.Flush(_file);
        var length = RandomAccess.GetLength(_file);
        if (length != _total)
        {
            if (length > _total) RandomAccess.SetLength(_file, _total);
            else return DownloadOutcome.Fail(DownloadErrorKind.Unknown, 0, $"size mismatch {length}/{_total}");
        }
        return DownloadOutcome.Done;
    }

    // ---------------- start / resume ----------------

    private async Task<bool> TryResumeAsync(CancellationToken ct)
    {
        if (_job.Segments.Count == 0 || string.IsNullOrEmpty(_job.TempPath) || !File.Exists(_job.TempPath)) return false;
        if (!_job.SupportsRanges || _job.TotalBytes <= 0) return false;

        // Validate that the server file is still the same one.
        using (var req = BuildRequest(_requestUrl, 0, 0))
        {
            HttpResponseMessage resp;
            try
            {
                resp = await SendForHeadersAsync(_client!, req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (_requestUrl != _job.Url)
            {
                _requestUrl = _job.Url;
                using var req2 = BuildRequest(_requestUrl, 0, 0);
                resp = await SendForHeadersAsync(_client!, req2, ct).ConfigureAwait(false);
            }
            using (resp)
            {
                if ((int)resp.StatusCode >= 400 && _requestUrl != _job.Url)
                {
                    // The redirect target may have expired; ask the original URL again.
                    _requestUrl = _job.Url;
                    return await TryResumeAsync(ct).ConfigureAwait(false);
                }
                ThrowForStatus(resp, firstRequest: true);
                var info = Describe(resp, _requestUrl);
                // On resume a validator that disappeared also counts as a change: a server that stops sending the
                // ETag may be serving different bytes of the same size, and old and new parts must never be mixed.
                if (!info.SupportsRanges || info.TotalBytes != _job.TotalBytes
                    || ValidatorChanged(info.ETag, _job.ETag) || ValidatorChanged(info.LastModified, _job.LastModified)
                    || ValidatorGone(info.ETag, _job.ETag) || ValidatorGone(info.LastModified, _job.LastModified))
                {
                    Status?.Invoke("changed");
                    Log.Info("Server file changed or no longer resumable; restarting from zero.");
                    return false;
                }
                _requestUrl = info.FinalUrl;
                _job.FinalUrl = info.FinalUrl;
                Probed?.Invoke(info);
            }
        }

        _file = File.OpenHandle(_job.TempPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        NativeFile.TrySetSparse(_file);
        if (RandomAccess.GetLength(_file) != _job.TotalBytes) RandomAccess.SetLength(_file, _job.TotalBytes);

        lock (_lock)
        {
            _total = _job.TotalBytes;
            _rangesOk = true;
            _segs.Clear();
            foreach (var s in _job.Segments.OrderBy(s => s.Start))
            {
                if (s.Start < 0 || s.End >= _total || s.Start > s.End + 1) continue;
                _segs.Add(new Seg { Start = s.Start, End = s.End, Pos = Math.Clamp(s.Position, s.Start, s.End + 1) });
            }
            if (!CoversWholeFile())
            {
                _segs.Clear();
                _file.Dispose();
                _file = null;
                return false;
            }
        }
        return true;
    }

    private bool CoversWholeFile()
    {
        long next = 0;
        foreach (var s in _segs)
        {
            if (s.Start != next) return false;
            next = s.End + 1;
        }
        return next == _total;
    }

    private static bool ValidatorChanged(string? now, string? before) =>
        !string.IsNullOrEmpty(now) && !string.IsNullOrEmpty(before) && !string.Equals(now, before, StringComparison.Ordinal);

    /// <summary>Takes from every limiter in turn; the smallest grant wins and the extra goes back to the others.</summary>
    /// <remarks><paramref name="grants"/>: one slot per limiter, allocated once per connection (no allocation per read).</remarks>
    private async ValueTask<int> AcquireAsync(int desired, int[] grants, CancellationToken ct)
    {
        var want = desired;
        for (var i = 0; i < _limiters.Length; i++) want = grants[i] = await _limiters[i].AcquireAsync(want, ct).ConfigureAwait(false);
        for (var i = 0; i < _limiters.Length; i++) _limiters[i].Refund(grants[i] - want);
        return want;
    }

    private void Refund(int bytes)
    {
        foreach (var limiter in _limiters) limiter.Refund(bytes);
    }

    /// <summary>Longest wait for a server's response headers. The client has no overall timeout (downloads may take
    /// hours), and the stall timer only watches the body, so a server that accepts the connection and then says
    /// nothing would otherwise hold a connection for ever.</summary>
    public static TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);

    private static async Task<HttpResponseMessage> SendForHeadersAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HeaderTimeout);
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            limit.CancelAfter(Timeout.InfiniteTimeSpan); // headers are here: the body is watched by the stall timer, not by this
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("no response headers");
        }
    }

    private static bool ValidatorGone(string? now, string? before) => string.IsNullOrEmpty(now) && !string.IsNullOrEmpty(before);

    private async Task<(HttpResponseMessage, Seg)> StartFreshAsync(CancellationToken ct)
    {
        HttpResponseMessage? resp = null;
        for (var attempt = 0; ; attempt++)
        {
            var req = BuildRequest(_job.Url, 0, _options.MaxRequestBytes > 0 ? _options.MaxRequestBytes - 1 : null);
            try
            {
                resp = await SendForHeadersAsync(_client!, req, ct).ConfigureAwait(false);
                if (IsRetryableStatus(resp.StatusCode) && attempt < Math.Min(_options.MaxRetries, 5))
                {
                    var wait = RetryAfter(resp) ?? Backoff(attempt + 1);
                    resp.Dispose();
                    Status?.Invoke("retry");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == null && attempt < Math.Min(_options.MaxRetries, 5) && !ct.IsCancellationRequested)
            {
                Status?.Invoke("retry");
                await Task.Delay(Backoff(attempt + 1), ct).ConfigureAwait(false);
            }
            finally
            {
                req.Dispose();
            }
        }

        try
        {
            ThrowForStatus(resp, firstRequest: true);
            var info = Describe(resp, _job.Url);
            _requestUrl = info.FinalUrl;
            _job.FinalUrl = info.FinalUrl;
            Probed?.Invoke(info);

            var path = _job.TempPath;
            if (string.IsNullOrEmpty(path))
            {
                path = ResolveTempPath?.Invoke(info) ?? throw new InvalidOperationException("No target path.");
                _job.TempPath = path;
            }

            _file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
            Seg first;
            lock (_lock)
            {
                _total = info.TotalBytes;
                _rangesOk = info.SupportsRanges && info.StatusCode == 206;
                _job.TotalBytes = _total;
                _job.SupportsRanges = _rangesOk;
                _job.ETag = info.ETag;
                _job.LastModified = info.LastModified;
                _segs.Clear();
                if (_total > 0)
                {
                    NativeFile.TrySetSparse(_file);
                    RandomAccess.SetLength(_file, _total);
                    first = new Seg { Start = 0, End = _total - 1, Pos = 0 };
                    _segs.Add(first);
                    if (_rangesOk) PreSplit(first);
                }
                else if (_total == 0)
                {
                    first = new Seg { Start = 0, End = -1, Pos = 0 };
                }
                else
                {
                    first = new Seg { Start = 0, End = UnknownEnd, Pos = 0 };
                }
                if (!_segs.Contains(first)) _segs.Add(first);
            }
            return (resp, first);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    // ---------------- segment scheduling ----------------

    /// <summary>
    /// Splits the file into N equal parts at the start. Only the first connection then has data in flight
    /// past its new end, instead of every early split throwing away a buffer's worth.
    /// </summary>
    private void PreSplit(Seg first)
    {
        var n = _targetConnections;
        if (n <= 1 || _total / n < _options.MinSplitBytes) return;
        long prev = 0;
        for (var i = 1; i < n; i++)
        {
            var b = _total * i / n;
            b -= b % 4096;
            if (b <= prev) continue;
            _segs[^1].End = b - 1; // the previous piece (first one is "first")
            _segs.Add(new Seg { Start = b, End = _total - 1, Pos = b });
            prev = b;
        }
    }

    /// <summary>Picks an idle unfinished segment, or splits the largest active one. Caller holds _lock.</summary>
    private bool TryTakeSegment(out Seg? seg)
    {
        seg = _segs.Where(s => !s.Active && !s.Done).OrderBy(s => s.Start).FirstOrDefault();
        if (seg != null) return true;
        if (!_rangesOk || _total <= 0) return false;

        Seg? best = null;
        foreach (var s in _segs)
            if (s.Active && !s.Done && s.Remaining >= 2 * _options.MinSplitBytes && (best == null || s.Remaining > best.Remaining))
                best = s;
        if (best == null) return false;

        var mid = best.Pos + best.Remaining / 2;
        mid -= mid % 4096;
        if (mid <= best.Pos) return false;
        var created = new Seg { Start = mid, End = best.End, Pos = mid };
        best.End = mid - 1;
        _segs.Insert(_segs.IndexOf(best) + 1, created);
        seg = created;
        return true;
    }

    private async Task RunWorkerAsync(Seg seg, HttpResponseMessage? initial, CancellationToken ct)
    {
        await Task.Yield();
        Interlocked.Increment(ref _activeWorkers);
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long pos, end;
                lock (_lock)
                {
                    if (seg.Done || _fatal != null)
                    {
                        initial?.Dispose();
                        return;
                    }
                    pos = seg.Pos;
                    end = seg.End;
                }

                var resp = initial;
                initial = null;
                try
                {
                    if (resp == null)
                    {
                        long? to = end == UnknownEnd ? null : end;
                        if (_rangesOk && _options.MaxRequestBytes > 0 && to.HasValue) to = Math.Min(to.Value, pos + _options.MaxRequestBytes - 1);
                        using var req = BuildRequest(_requestUrl, _rangesOk || pos > 0 ? pos : null, _rangesOk ? to : null);
                        resp = await SendForHeadersAsync(_client!, req, ct).ConfigureAwait(false);
                        ValidateSegmentResponse(resp, pos);
                    }

                    // A 206 covers only the range it names; stop there instead of waiting for more.
                    var requestEnd = resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.To is long rangeTo ? rangeTo : long.MaxValue;
                    var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var progressed = await PumpAsync(seg, stream, buffer, ct, requestEnd).ConfigureAwait(false);
                    if (progressed) failures = 0;

                    lock (_lock)
                    {
                        if (seg.Done) return;
                        // This piece is complete; ask for the next one of the same segment.
                        if (requestEnd != long.MaxValue && seg.Pos > requestEnd) continue;
                    }
                    // Stream ended early (server closed the connection) — retry from the current position.
                    throw new RetryableException("connection closed early");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (FatalException fx)
                {
                    SetFatal(fx.Outcome);
                    return;
                }
                catch (Exception ex) when (NativeFile.IsDiskFull(ex))
                {
                    SetFatal(DownloadOutcome.Fail(DownloadErrorKind.DiskFull));
                    return;
                }
                catch (Exception ex) when (ex is RetryableException or HttpRequestException or IOException or OperationCanceledException or TimeoutException)
                {
                    failures++;
                    var rx = ex as RetryableException;
                    if (rx?.Throttled == true)
                    {
                        // Server is limiting us: use fewer connections from now on.
                        var others = ActiveWorkers - 1;
                        if (others > 0)
                        {
                            TargetConnections = Math.Max(1, Math.Min(_targetConnections - 1, others));
                            // Give the segment back; a busy worker will take it later.
                            return;
                        }
                    }
                    if (failures > _options.MaxRetries)
                    {
                        SetFatal(ex is HttpRequestException { StatusCode: { } sc }
                            ? DownloadOutcome.Fail(DownloadErrorKind.HttpStatus, (int)sc)
                            : DownloadOutcome.Fail(DownloadErrorKind.Network, 0, ex.Message));
                        return;
                    }
                    Status?.Invoke("retry");
                    await Task.Delay(rx?.RetryAfter ?? Backoff(failures), ct).ConfigureAwait(false);
                }
                finally
                {
                    resp?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            lock (_lock) seg.Active = false;
            Interlocked.Decrement(ref _activeWorkers);
        }
    }

    /// <summary>Longest time read data waits in memory before it is written (keeps progress and resume points fresh).</summary>
    private static readonly long FlushTicks = Stopwatch.Frequency / 4;

    /// <summary>
    /// Copies the response into the file at the segment position. Returns true if any bytes were written.
    /// Network reads arrive in small pieces (often 16 KB); they are gathered in the buffer and written in one
    /// go, so a fast download makes far fewer disk writes. The stall timer is re-armed about once a second.
    /// </summary>
    private async Task<bool> PumpAsync(Seg seg, Stream stream, byte[] buffer, CancellationToken ct, long requestEnd = long.MaxValue)
    {
        var any = false;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_options.StallTimeout);
        var armed = Stopwatch.GetTimestamp();
        var grants = new int[_limiters.Length]; // scratch for AcquireAsync, once per response
        while (true)
        {
            long pos, end;
            lock (_lock)
            {
                pos = seg.Pos;
                end = Math.Min(seg.End, requestEnd);
            }
            if (pos > end) return any;

            var room = (int)Math.Min(buffer.Length, end - pos + 1);
            var filled = 0;
            var eof = false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                while (filled < room)
                {
                    var want = await AcquireAsync(room - filled, grants, ct).ConfigureAwait(false);
                    var now = Stopwatch.GetTimestamp();
                    if (now - armed > Stopwatch.Frequency)
                    {
                        stall.CancelAfter(_options.StallTimeout);
                        armed = now;
                    }
                    int n;
                    try
                    {
                        n = await stream.ReadAsync(buffer.AsMemory(filled, want), stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new RetryableException("stalled");
                    }
                    Refund(want - n); // a short read: the rest of the grant goes back
                    if (n == 0) { eof = true; break; }
                    filled += n;
                    if (Stopwatch.GetTimestamp() - started > FlushTicks) break;
                }
            }
            catch when (filled > 0 && !ct.IsCancellationRequested)
            {
                // Keep what already arrived; the retry continues after it.
                await WriteAsync(seg, buffer, pos, filled, ct).ConfigureAwait(false);
                throw;
            }

            if (filled > 0)
            {
                await WriteAsync(seg, buffer, pos, filled, ct).ConfigureAwait(false);
                any = true;
            }
            if (eof)
            {
                if (end == UnknownEnd)
                {
                    lock (_lock) seg.End = seg.Pos - 1; // EOF defines the size
                }
                return any;
            }
        }
    }

    private async Task WriteAsync(Seg seg, byte[] buffer, long pos, int count, CancellationToken ct)
    {
        await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, count), pos, ct).ConfigureAwait(false);
        lock (_lock)
        {
            // End may have shrunk while we read (segment was split): never count past it.
            var next = pos + count;
            seg.Pos = seg.End == UnknownEnd ? next : Math.Min(next, seg.End + 1);
        }
    }

    private void ValidateSegmentResponse(HttpResponseMessage resp, long pos)
    {
        ThrowForStatus(resp, firstRequest: false);
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.From != pos) throw new RetryableException("unexpected range");
            if (_total > 0 && cr.Length.HasValue && cr.Length.Value != _total)
                throw new FatalException(DownloadOutcome.Fail(DownloadErrorKind.ContentChanged));
            if (ValidatorChanged(resp.Headers.ETag?.ToString(), _job.ETag) || ValidatorChanged(resp.Content.Headers.LastModified?.ToString("R"), _job.LastModified))
                throw new FatalException(DownloadOutcome.Fail(DownloadErrorKind.ContentChanged));
            return;
        }
        if (resp.StatusCode == HttpStatusCode.OK && pos == 0) return;
        // A 200 for a range that does not start at 0 means ranges stopped working.
        throw new FatalException(DownloadOutcome.Fail(DownloadErrorKind.RangeUnsupported));
    }

    private void ThrowForStatus(HttpResponseMessage resp, bool firstRequest)
    {
        var code = (int)resp.StatusCode;
        if (code < 400) return;
        if (IsRetryableStatus(resp.StatusCode))
            throw new RetryableException($"HTTP {code}", RetryAfter(resp), throttled: code == 429 || code == 503);
        if (code == 416)
            throw new FatalException(DownloadOutcome.Fail(DownloadErrorKind.ContentChanged, code));
        if (!firstRequest && code is 403 or 401 && ActiveWorkers > 1)
        {
            // Some servers answer 403 when a client opens too many connections.
            throw new RetryableException($"HTTP {code}", TimeSpan.FromSeconds(3), throttled: true);
        }
        throw new FatalException(DownloadOutcome.Fail(DownloadErrorKind.HttpStatus, code));
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        (int)status is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra == null) return null;
        TimeSpan? t = ra.Delta ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : null);
        if (t == null) return null;
        return TimeSpan.FromSeconds(Math.Clamp(t.Value.TotalSeconds, 1, 120));
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5)));
        seconds *= 0.8 + Random.Shared.NextDouble() * 0.4; // jitter
        return TimeSpan.FromSeconds(seconds * _options.RetryDelayScale);
    }

    private void SetFatal(DownloadOutcome outcome)
    {
        lock (_lock) _fatal ??= outcome;
    }
}
