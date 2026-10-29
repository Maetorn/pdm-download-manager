using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PDM.Tests;

/// <summary>
/// Tiny HTTP/1.1 server on 127.0.0.1 for engine tests. Content is a deterministic function of the byte
/// position, so any file's SHA-256 can be computed independently.
/// Paths: /file/{size}?mode=...  modes (comma separated):
///   norange   – ignore Range, always 200
///   drop      – close the connection after 256 KB for the first 6 requests
///   r429      – answer the first 3 requests with 429 + Retry-After: 1
///   chunked   – no Content-Length, chunked transfer, ignore Range
///   etag2     – ETag/content seed "v2" (simulates a changed file)
///   noetag    – content seed "v2" with no ETag at all (a changed file whose server dropped the validator)
///   slow      – 64 KB pauses of 20 ms
///   name      – Content-Disposition with a UTF-8 file name
///   cookie    – require Cookie "session=abc" (403 otherwise)
///   forbidden – always 403
///   hang      – accepts the request and never answers (no headers)
/// /redirect?to=URL – 302 to URL;   /missing – 404
/// /auth/{size} – like /file/{size} but needs Basic "user:pass" (401 otherwise)
/// /stall.html – 200 text/html with a Content-Length, a few bytes of body and then silence (a server that stops mid-body)
/// /site/*.html – a small web site for the grabber; /files/{name} – small files; /dl.php – a file behind a page-like link
/// Absolute-form requests ("GET http://host/… HTTP/1.1") are served as a proxy would (counted in ProxyRequests);
/// with RequireProxyLogin they need Proxy-Authorization "proxy:secret" (407 otherwise).
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, int> _counters = new();
    private long _bytesServed;
    private int _activeBodies;
    private int _maxConcurrent;

    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}";
    public string BaseLocalhost => $"http://localhost:{Port}";
    public long BytesServed => Interlocked.Read(ref _bytesServed);
    /// <summary>Most response bodies that were being sent at the same time since the last reset.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
    public ConcurrentQueue<string> CookieLog { get; } = new();
    public int Requests;
    public int ProxyRequests;
    public bool RequireProxyLogin { get; set; }
    /// <summary>Authorization header of each /auth request ("" when none was sent).</summary>
    public ConcurrentQueue<string> AuthLog { get; } = new();

    private static readonly Dictionary<string, string> Site = new()
    {
        ["/site/index.html"] =
            "<html><head><title>t</title></head><body><a href=\"page2.html\">2</a> <a href='/files/a.zip'>a</a> " +
            "<a href=\"https://other.example/away.html\">x</a> <img src=\"/files/pic.jpg\"> <a href=\"/files/style.css\">c</a> " +
            "<a href=\"#top\">t</a> <a href=\"javascript:void(0)\">j</a> <a href=\"/dl.php?id=7&amp;x=1\">d</a></body></html>",
        ["/site/page2.html"] = "<html><body><a href=\"/files/b.pdf\">b</a> <a href=\"page3.html\">3</a> <a href=\"index.html\">back</a></body></html>",
        ["/site/page3.html"] = "<html><head><base href=\"/files/\"></head><body><a href=\"c.zip\">c</a></body></html>",
    };

    public TestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public void ResetCounters()
    {
        _counters.Clear();
        Interlocked.Exchange(ref _bytesServed, 0);
        Interlocked.Exchange(ref _maxConcurrent, 0);
        Requests = 0;
        ProxyRequests = 0;
        AuthLog.Clear();
        CookieLog.Clear();
    }

    public static byte ByteAt(long i, int seed) => (byte)((i * 2654435761L >> 13) ^ (i >> 7) ^ seed);

    public static string ExpectedSha(long size, int seed = 0)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 16];
        for (long pos = 0; pos < size; pos += buf.Length)
        {
            var n = (int)Math.Min(buf.Length, size - pos);
            for (var k = 0; k < n; k++) buf[k] = ByteAt(pos + k, seed);
            sha.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                while (true)
                {
                    var head = await ReadHead(stream);
                    if (head == null) return;
                    Interlocked.Increment(ref Requests);
                    var keep = await Handle(stream, head);
                    if (!keep) return;
                }
            }
            catch { }
        }
    }

    private static async Task<string?> ReadHead(NetworkStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') return sb.ToString();
            if (sb.Length > 65536) return null;
        }
    }

    private async Task<bool> Handle(NetworkStream s, string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        var target = parts[1];
        var headers = lines.Skip(1).Where(l => l.Contains(':'))
            .GroupBy(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First()[(g.First().IndexOf(':') + 1)..].Trim());
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // Absolute form: the client talks to us as its proxy.
            Interlocked.Increment(ref ProxyRequests);
            if (RequireProxyLogin && headers.GetValueOrDefault("proxy-authorization", "") != "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("proxy:secret")))
            {
                await Write(s, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 0\r\n\r\n");
                return true;
            }
            target = new Uri(target).PathAndQuery;
        }
        var uri = new Uri("http://x" + target);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var host = headers.GetValueOrDefault("host", "");
        CookieLog.Enqueue(host + "|" + headers.GetValueOrDefault("cookie", ""));

        if (uri.AbsolutePath == "/redirect")
        {
            await Write(s, $"HTTP/1.1 302 Found\r\nLocation: {query["to"]}\r\nContent-Length: 0\r\n\r\n");
            return true;
        }
        if (uri.AbsolutePath.StartsWith("/auth/"))
        {
            var auth = headers.GetValueOrDefault("authorization", "");
            AuthLog.Enqueue(auth);
            if (auth != "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass")))
            {
                await Write(s, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"t\"\r\nContent-Length: 0\r\n\r\n");
                return true;
            }
            uri = new Uri("http://x/file/" + uri.AbsolutePath["/auth/".Length..] + uri.Query);
        }
        if (Site.TryGetValue(uri.AbsolutePath, out var page))
        {
            var body = Encoding.UTF8.GetBytes(page);
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n");
            await s.WriteAsync(body);
            return true;
        }
        if (uri.AbsolutePath == "/stall.html")
        {
            await Write(s, "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100000\r\n\r\n<html><body>");
            try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); } catch (OperationCanceledException) { }
            return false;
        }
        if (uri.AbsolutePath.StartsWith("/files/") || uri.AbsolutePath == "/dl.php")
        {
            var disposition = uri.AbsolutePath == "/dl.php" ? "Content-Disposition: attachment; filename=\"d.zip\"\r\n" : "";
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n{disposition}Content-Length: 4\r\n\r\ndata");
            return true;
        }
        if (!uri.AbsolutePath.StartsWith("/file/"))
        {
            await Write(s, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
            return true;
        }

        var size = long.Parse(uri.AbsolutePath["/file/".Length..].Split('/')[0]);
        var modes = (query["mode"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var key = uri.AbsolutePath + "?" + query["mode"];
        var count = _counters.AddOrUpdate(key, 1, (_, v) => v + 1);
        var seed = modes.Contains("etag2") || modes.Contains("noetag") ? 2 : 0;
        var etag = modes.Contains("etag2") ? "\"v2\"" : "\"v1\"";

        if (modes.Contains("hang"))
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); } catch (OperationCanceledException) { }
            return false;
        }
        if (modes.Contains("forbidden") || (modes.Contains("cookie") && !headers.GetValueOrDefault("cookie", "").Contains("session=abc")))
        {
            await Write(s, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
            return true;
        }
        if (modes.Contains("r429") && count <= 3)
        {
            await Write(s, "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 1\r\nContent-Length: 0\r\n\r\n");
            return true;
        }

        long from = 0, to = size - 1;
        var partial = false;
        if (!modes.Contains("norange") && !modes.Contains("chunked") && headers.TryGetValue("range", out var range) && range.StartsWith("bytes="))
        {
            var r = range[6..].Split('-');
            from = long.Parse(r[0]);
            if (r.Length > 1 && r[1].Length > 0) to = Math.Min(long.Parse(r[1]), size - 1);
            if (from >= size)
            {
                await Write(s, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{size}\r\nContent-Length: 0\r\n\r\n");
                return true;
            }
            partial = true;
        }

        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n");
        if (!modes.Contains("noetag")) sb.Append($"ETag: {etag}\r\n");
        if (!modes.Contains("norange") && !modes.Contains("chunked")) sb.Append("Accept-Ranges: bytes\r\n");
        if (modes.Contains("name")) sb.Append("Content-Disposition: attachment; filename=\"fallback.bin\"; filename*=UTF-8''na%C3%AFve%20caf%C3%A9%20%C3%BC%C3%9F.bin\r\n");
        if (partial) sb.Append($"Content-Range: bytes {from}-{to}/{size}\r\n");
        if (modes.Contains("chunked")) sb.Append("Transfer-Encoding: chunked\r\n");
        else sb.Append($"Content-Length: {to - from + 1}\r\n");
        sb.Append("\r\n");
        await Write(s, sb.ToString());

        var dropAfter = modes.Contains("drop") && count <= 6 ? 256 * 1024 : long.MaxValue;
        var buf = new byte[64 * 1024];
        long sent = 0;
        var active = Interlocked.Increment(ref _activeBodies);
        for (int seen; (seen = Volatile.Read(ref _maxConcurrent)) < active && Interlocked.CompareExchange(ref _maxConcurrent, active, seen) != seen;) { }
        try
        {
        for (var pos = from; pos <= to;)
        {
            var n = (int)Math.Min(buf.Length, to - pos + 1);
            for (var k = 0; k < n; k++) buf[k] = ByteAt(pos + k, seed);
            if (sent + n > dropAfter)
            {
                await s.WriteAsync(buf.AsMemory(0, (int)(dropAfter - sent)));
                Interlocked.Add(ref _bytesServed, dropAfter - sent);
                return false; // abrupt close
            }
            if (modes.Contains("chunked"))
            {
                await Write(s, n.ToString("x") + "\r\n");
                await s.WriteAsync(buf.AsMemory(0, n));
                await Write(s, "\r\n");
            }
            else
            {
                await s.WriteAsync(buf.AsMemory(0, n));
            }
            Interlocked.Add(ref _bytesServed, n);
            sent += n;
            pos += n;
            if (modes.Contains("slow")) await Task.Delay(20);
        }
        }
        finally
        {
            Interlocked.Decrement(ref _activeBodies);
        }
        if (modes.Contains("chunked")) await Write(s, "0\r\n\r\n");
        return true;
    }

    private static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
