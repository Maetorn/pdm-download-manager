using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PDM.Core.Engine;
using PDM.Core.Security;

namespace PDM.Core;

public sealed class GrabberOptions
{
    public string StartUrl { get; set; } = "";
    /// <summary>How many link steps away from the start page to follow pages (0 = the start page only).</summary>
    public int Depth { get; set; } = 1;
    /// <summary>Follow pages on the start page's site only (its host and subdomains). Files may be anywhere.</summary>
    public bool SameSite { get; set; } = true;
    public int MaxPages { get; set; } = 200;
    /// <summary>File types to collect (".zip", "pdf" …); empty = every link that is not a web page.</summary>
    public List<string> Extensions { get; set; } = new();
    public string? UserAgent { get; set; }
    public int Concurrency { get; set; } = 4;
}

public sealed record GrabbedFile(string Url, string FoundOn);

/// <summary>
/// IDM-style site grabber: reads the start page, follows its links to other pages up to the chosen depth
/// and collects links to files. Only reads http/https pages (HTML, at most 4 MB each), never runs anything.
/// </summary>
public sealed partial class SiteGrabber
{
    public const int MaxFiles = 10_000;
    private const int MaxPageBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".html", ".htm", ".xhtml", ".shtml", ".php", ".asp", ".aspx", ".jsp", ".jspx", ".cfm", ".cgi", ".pl",
    };

    /// <summary>Page furniture that is not worth downloading when no file types are chosen.</summary>
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map", ".json", ".xml", ".rss", ".atom", ".ico", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".svg", ".webmanifest",
    };

    [GeneratedRegex(@"<a\s[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))|<(?:img|source|video|audio|embed|iframe|frame)\s[^>]*?src\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))|<base\s[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    /// <summary>Pages read so far and files found so far (called from worker threads).</summary>
    public Action<int, int>? Progress { get; set; }

    /// <summary>Runs until done or <paramref name="ct"/> is cancelled; either way returns the files found.</summary>
    public async Task<List<GrabbedFile>> RunAsync(GrabberOptions o, CancellationToken ct)
    {
        if (!FileNames.IsHttpUrl(o.StartUrl, out var start)) throw new ArgumentException("Only http and https addresses can be grabbed.");
        var exts = o.Extensions.Select(e => e.Trim().TrimStart('*')).Where(e => e.Length > 0).Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var site = SiteOf(start!.Host);

        using var http = HttpDownloader.CreateClient(null);
        http.Timeout = TimeSpan.FromSeconds(30);
        var ua = string.IsNullOrWhiteSpace(o.UserAgent) ? EngineOptions.DefaultUserAgent : o.UserAgent;

        var visited = new HashSet<string>(StringComparer.Ordinal) { Key(start) };
        var files = new Dictionary<string, GrabbedFile>(StringComparer.Ordinal);
        var foundOn = new Dictionary<string, string>(StringComparer.Ordinal);
        var level = new List<Uri> { start };
        var pages = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(o.Concurrency, 1, 8));

        for (var depth = 0; level.Count > 0 && depth <= o.Depth; depth++)
        {
            var next = new List<Uri>();
            var tasks = level.Select(async page =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref pages) >= o.MaxPages) return;
                    var (links, fileName) = await ReadPageAsync(http, page, ua, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref pages);
                    lock (files)
                    {
                        // A page-looking link that answered with a file (download.php?id=…) is a file.
                        if (links == null && fileName != null && page != start && WantedName(fileName, exts) && files.Count < MaxFiles)
                            files.TryAdd(page.AbsoluteUri, new GrabbedFile(page.AbsoluteUri, foundOn.GetValueOrDefault(page.AbsoluteUri, start.AbsoluteUri)));
                        foreach (var link in links ?? new())
                        {
                            if (IsPage(link))
                            {
                                if (depth < o.Depth && (!o.SameSite || SiteOf(link.Host) == site) && visited.Add(Key(link)))
                                {
                                    next.Add(link);
                                    foundOn.TryAdd(link.AbsoluteUri, page.AbsoluteUri);
                                }
                            }
                            else if (Wanted(link, exts) && files.Count < MaxFiles && !files.ContainsKey(link.AbsoluteUri))
                            {
                                files[link.AbsoluteUri] = new GrabbedFile(link.AbsoluteUri, page.AbsoluteUri);
                            }
                        }
                        Progress?.Invoke(pages, files.Count);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Info($"Grabber: {FileNames.Redact(page.AbsoluteUri)}: {ex.GetType().Name}"); }
                finally { gate.Release(); }
            }).ToList();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } // stopped: keep what was found
            level = next.Take(Math.Max(0, o.MaxPages - pages)).ToList();
        }
        lock (files) return files.Values.ToList();
    }

    /// <summary>Links on one HTML page, absolute, without #fragment; for a non-HTML answer, no links but the file's name.</summary>
    private static async Task<(List<Uri>? Links, string? FileName)> ReadPageAsync(HttpClient http, Uri page, string ua, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, page);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK) return (new(), null);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var final0 = resp.RequestMessage?.RequestUri ?? page;
            var name = FileNames.FromContentDisposition(resp.Content.Headers.ContentDisposition?.ToString()) ?? FileNames.FromUrl(final0.AbsoluteUri);
            return (null, name);
        }
        if (resp.Content.Headers.ContentLength > MaxPageBytes) return (new(), null);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxPageBytes) break;
            buffer.Write(chunk, 0, n);
        }
        Encoding enc;
        try { enc = Encoding.GetEncoding(resp.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8"); }
        catch { enc = Encoding.UTF8; }
        var html = enc.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var final = resp.RequestMessage?.RequestUri ?? page;
        return (ExtractLinks(html, final), null);
    }

    /// <summary>href/src links in HTML, resolved against the page (and its &lt;base href&gt;).</summary>
    public static List<Uri> ExtractLinks(string html, Uri page)
    {
        var baseUri = page;
        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in LinkPattern().Matches(html))
        {
            var raw = Enumerable.Range(1, 8).Select(i => m.Groups[i]).FirstOrDefault(g => g.Success)?.Value;
            if (raw == null) continue;
            raw = WebUtility.HtmlDecode(raw).Trim();
            if (m.Groups[7].Success || m.Groups[8].Success)
            {
                if (Uri.TryCreate(page, raw, out var b) && b.Scheme is "http" or "https") baseUri = b;
                continue;
            }
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            if (!Uri.TryCreate(baseUri, raw, out var u) || u.Scheme is not ("http" or "https")) continue;
            var clean = new UriBuilder(u) { Fragment = "" }.Uri;
            if (seen.Add(clean.AbsoluteUri)) result.Add(clean);
        }
        return result;
    }

    private static bool IsPage(Uri u) => PageExtensions.Contains(Path.GetExtension(u.AbsolutePath));

    private static bool Wanted(Uri u, HashSet<string> exts) => WantedName(u.AbsolutePath, exts);

    private static bool WantedName(string name, HashSet<string> exts)
    {
        if (exts.Count > 0) return exts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return !AssetExtensions.Contains(Path.GetExtension(name));
    }

    /// <summary>"www.example.com" and "files.example.com" are the same site: the last two labels.</summary>
    private static string SiteOf(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        var parts = host.ToLowerInvariant().Split('.');
        if (parts.Length <= 2) return host.ToLowerInvariant();
        // Two-letter country domains with a second level (example.com.tr, example.co.uk) keep three labels.
        var keep = parts[^1].Length == 2 && parts[^2].Length <= 3 ? 3 : 2;
        return string.Join('.', parts[^Math.Min(keep, parts.Length)..]);
    }

    private static string Key(Uri u) => u.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
}
