using System.Text.Json;
using PDM.Core.Engine;
using PDM.Core.Security;

namespace PDM.Core.Ipc;

public static class ExtensionInfo
{
    /// <summary>Fixed ID of the bundled extension (derived from the "key" in its manifest).</summary>
    public const string Id = "dloanbeiidcfnnmdmgpcdhbkenjjangb";
    public const string Origin = "chrome-extension://" + Id + "/";
    /// <summary>Add-on ID of the Firefox build (browser_specific_settings.gecko.id); Firefox passes it to the host.</summary>
    public const string FirefoxId = "pdm-download-manager@maetorn";
}

public abstract record BridgeMessage;
public sealed record PingMessage : BridgeMessage;
public sealed record ShowMessage : BridgeMessage;
/// <summary>The extension saw that PDM carries a newer version of it (Firefox: installed from the signed .xpi).</summary>
public sealed record UpdateExtensionMessage(bool Firefox) : BridgeMessage;
public sealed record ArgsMessage(string[] Args) : BridgeMessage;
public sealed record DownloadMessage(List<AddRequest> Items, bool Interactive, string Source) : BridgeMessage;
public sealed record MediaLink(string Url, string? Mime, long Size, string? Label);
/// <summary>PageUrl is the page (or embedded player frame) to give yt-dlp; Referrer is the top page when they differ.</summary>
public sealed record VideoMessage(string PageUrl, string? Title, List<MediaLink> Media, string? UserAgent, List<CookieData>? Cookies, string? Referrer = null) : BridgeMessage;

public sealed class BridgeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Parses and validates messages from the native host / second app instance. Everything is treated as
/// untrusted: types are checked, sizes are capped, only http(s) links are accepted.
/// </summary>
public static class BridgeProtocol
{
    public const int MaxItems = 500;
    public const int MaxStringLength = 16 * 1024;

    public static BridgeMessage Parse(byte[] utf8, IReadOnlyCollection<string> allowedOrigins)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new BridgeException("bad-json"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BridgeException("bad-json");

            // Messages relayed by the native host are wrapped with the calling extension's origin.
            if (root.TryGetProperty("origin", out var originEl))
            {
                var origin = originEl.ValueKind == JsonValueKind.String ? originEl.GetString() : null;
                if (origin == null || !allowedOrigins.Contains(origin)) throw new BridgeException("origin");
                if (!root.TryGetProperty("msg", out var msg) || msg.ValueKind != JsonValueKind.Object) throw new BridgeException("bad-json");
                return ParseExtensionMessage(msg, origin);
            }

            var type = Str(root, "type");
            return type switch
            {
                "args" => new ArgsMessage(root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(32).ToArray()
                    : []),
                "show" => new ShowMessage(),
                "ping" => new PingMessage(),
                _ => throw new BridgeException("type"),
            };
        }
    }

    private static BridgeMessage ParseExtensionMessage(JsonElement m, string origin)
    {
        var type = Str(m, "type");
        switch (type)
        {
            case "ping":
                return new PingMessage();
            case "updateExtension":
                return new UpdateExtensionMessage(origin == ExtensionInfo.FirefoxId);
            case "show":
                return new ShowMessage();
            case "download":
            {
                if (!m.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new BridgeException("items");
                var ua = Str(m, "userAgent", 1024);
                var list = new List<AddRequest>();
                foreach (var e in arr.EnumerateArray().Take(MaxItems))
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var url = Str(e, "url");
                    if (!FileNames.IsHttpUrl(url, out _)) continue;
                    list.Add(new AddRequest
                    {
                        Url = url!,
                        FileName = NullIfEmpty(Str(e, "filename", 1024)),
                        Referrer = HttpOrNull(Str(e, "referrer")),
                        PageUrl = HttpOrNull(Str(e, "pageUrl")),
                        SizeHint = Long(e, "size"),
                        ContentType = Str(e, "mime", 200),
                        UserAgent = Str(e, "userAgent", 1024) ?? ua,
                        Cookies = ParseCookies(e),
                    });
                }
                if (list.Count == 0) throw new BridgeException("no-valid-items");
                var interactive = m.TryGetProperty("interactive", out var i) && i.ValueKind == JsonValueKind.True;
                return new DownloadMessage(list, interactive, Str(m, "source", 32) ?? "capture");
            }
            case "video":
            {
                var page = Str(m, "pageUrl");
                if (!FileNames.IsHttpUrl(page, out _)) throw new BridgeException("url");
                var media = new List<MediaLink>();
                if (m.TryGetProperty("media", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray().Take(50))
                    {
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        var url = Str(e, "url");
                        if (!FileNames.IsHttpUrl(url, out _)) continue;
                        media.Add(new MediaLink(url!, Str(e, "mime", 200), Long(e, "size"), Str(e, "label", 200)));
                    }
                }
                return new VideoMessage(page!, Str(m, "title", 500), media, Str(m, "userAgent", 1024), ParseCookies(m), HttpOrNull(Str(m, "referrer")));
            }
            default:
                throw new BridgeException("type");
        }
    }

    private static List<CookieData>? ParseCookies(JsonElement e)
    {
        if (!e.TryGetProperty("cookies", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<CookieData>();
        foreach (var c in arr.EnumerateArray().Take(Cookies.MaxCookies))
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var name = Str(c, "name", 4096);
            var domain = Str(c, "domain", 255);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain)) continue;
            list.Add(new CookieData
            {
                Name = name,
                Value = Str(c, "value", 8192) ?? "",
                Domain = domain,
                Path = Str(c, "path", 1024) ?? "/",
                Secure = c.TryGetProperty("secure", out var s) && s.ValueKind == JsonValueKind.True,
                HostOnly = c.TryGetProperty("hostOnly", out var h) && h.ValueKind == JsonValueKind.True,
                HttpOnly = c.TryGetProperty("httpOnly", out var ho) && ho.ValueKind == JsonValueKind.True,
            });
        }
        return list.Count > 0 ? list : null;
    }

    private static string? Str(JsonElement e, string name, int max = MaxStringLength)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return s != null && s.Length <= max ? s : null;
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) && l >= 0 ? l : -1;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? HttpOrNull(string? s) => FileNames.IsHttpUrl(s, out _) ? s : null;

    /// <summary>Version of the extension files PDM has put on disk; the extension reloads itself when it is older.</summary>
    public static string? ExtensionVersion { get; set; }

    public static byte[] Ok(int accepted = 0)
    {
        var d = new Dictionary<string, object> { ["ok"] = true, ["accepted"] = accepted, ["version"] = AppPaths.Version };
        if (ExtensionVersion is { } ev) d["extVersion"] = ev;
        return JsonSerializer.SerializeToUtf8Bytes(d);
    }

    public static byte[] Error(string code) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["ok"] = false, ["error"] = code });
}
