using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PDM.Core.Engine;

/// <summary>A cookie as sent by the browser extension (chrome.cookies.Cookie subset).</summary>
public sealed class CookieData
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "/";
    [JsonPropertyName("secure")] public bool Secure { get; set; }
    [JsonPropertyName("hostOnly")] public bool HostOnly { get; set; }
    [JsonPropertyName("httpOnly")] public bool HttpOnly { get; set; }
}

public static class Cookies
{
    public const int MaxCookies = 300;

    /// <summary>
    /// Builds a container so cookies follow normal domain/path/secure rules, including across redirects.
    /// Cookies that .NET considers malformed are skipped.
    /// </summary>
    public static CookieContainer? Build(IEnumerable<CookieData>? cookies)
    {
        if (cookies == null) return null;
        var container = new CookieContainer(MaxCookies, MaxCookies, 16 * 1024);
        var any = false;
        foreach (var c in cookies.Take(MaxCookies))
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Domain)) continue;
            var domain = c.Domain.Trim();
            var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
            try
            {
                if (c.HostOnly)
                {
                    // No Domain attribute + Add(uri, cookie) = host-only cookie (not sent to subdomains).
                    var host = domain.TrimStart('.');
                    if (Uri.CheckHostName(host) == UriHostNameType.Unknown) continue;
                    var cookie = new Cookie(c.Name, EscapeValue(c.Value), path) { Secure = c.Secure, HttpOnly = c.HttpOnly };
                    container.Add(new Uri("https://" + host + "/"), cookie);
                }
                else
                {
                    if (!domain.StartsWith('.')) domain = "." + domain;
                    container.Add(new Cookie(c.Name, EscapeValue(c.Value), path, domain) { Secure = c.Secure, HttpOnly = c.HttpOnly });
                }
                any = true;
            }
            catch (Exception ex) when (ex is CookieException or UriFormatException)
            {
                // malformed for .NET; skip
            }
        }
        return any ? container : null;
    }

    /// <summary>
    /// Netscape cookies.txt for yt-dlp (--cookies). Host-only cookies keep their exact host; domain cookies get the
    /// leading dot and TRUE, as the format requires. Entries with tabs or line breaks are left out.
    /// </summary>
    public static string ToNetscape(IEnumerable<CookieData> cookies, DateTimeOffset expires)
    {
        var sb = new System.Text.StringBuilder("# Netscape HTTP Cookie File\n");
        var exp = expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        static bool Clean(string s) => s.IndexOfAny(['\t', '\r', '\n']) < 0;
        foreach (var c in cookies.Take(MaxCookies))
        {
            var domain = c.Domain.Trim();
            var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
            if (domain.Length == 0 || c.Name.Length == 0 || !Clean(domain) || !Clean(c.Name) || !Clean(c.Value ?? "") || !Clean(path)) continue;
            var host = domain.TrimStart('.');
            var line = c.HostOnly
                ? $"{host}\tFALSE\t{path}\t{(c.Secure ? "TRUE" : "FALSE")}\t{exp}\t{c.Name}\t{c.Value}"
                : $".{host}\tTRUE\t{path}\t{(c.Secure ? "TRUE" : "FALSE")}\t{exp}\t{c.Name}\t{c.Value}";
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static string EscapeValue(string v) =>
        v.IndexOfAny([';', ',']) >= 0 && !(v.StartsWith('"') && v.EndsWith('"')) ? "\"" + v.Replace("\"", "") + "\"" : v;

    public static string Serialize(List<CookieData> cookies) => JsonSerializer.Serialize(cookies, PdmJson.Default.ListCookieData);

    public static List<CookieData>? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize(json, PdmJson.Default.ListCookieData); }
        catch { return null; }
    }
}
