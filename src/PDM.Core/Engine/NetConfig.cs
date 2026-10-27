using System.Net;
using System.Net.Http;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM.Core.Engine;

public enum ProxyMode
{
    /// <summary>Windows' proxy settings (Internet Options).</summary>
    System,
    /// <summary>Always connect directly.</summary>
    None,
    /// <summary>The address in settings (http or socks5).</summary>
    Custom,
}

/// <summary>Username and password for one site (HTTP Basic/Digest). The password is DPAPI-encrypted.</summary>
public sealed class SiteLogin
{
    /// <summary>"example.com" (also matches its subdomains) or "files.example.com".</summary>
    public string Host { get; set; } = "";
    public string User { get; set; } = "";
    public string? ProtectedPassword { get; set; }
}

/// <summary>
/// Proxy and site logins for every connection PDM makes (downloads, probes, the site grabber, tool and app updates)
/// and the proxy for yt-dlp. Set from settings with <see cref="Configure"/>; read when a connection is made.
/// </summary>
public static class NetConfig
{
    private sealed record Snapshot(ProxyMode Mode, Uri? Proxy, NetworkCredential? ProxyLogin, IReadOnlyList<SiteLogin> Logins);

    private static volatile Snapshot _current = new(ProxyMode.System, null, null, Array.Empty<SiteLogin>());

    /// <summary>Local addresses skip the proxy (tests turn this off to send loopback traffic through one).</summary>
    public static bool BypassLocal { get; set; } = true;

    public static ProxyMode Mode => _current.Mode;
    public static Uri? ProxyAddress => _current.Proxy;

    public static void Configure(AppSettings s)
    {
        var proxy = s.ProxyMode == ProxyMode.Custom ? ParseProxy(s.ProxyAddress) : null;
        NetworkCredential? login = null;
        if (proxy != null && !string.IsNullOrEmpty(s.ProxyUser))
            login = new NetworkCredential(s.ProxyUser, Dpapi.Unprotect(s.ProtectedProxyPassword) ?? "");
        var mode = s.ProxyMode == ProxyMode.Custom && proxy == null ? ProxyMode.System : s.ProxyMode;
        _current = new Snapshot(mode, proxy, login, s.Logins.Where(l => !string.IsNullOrWhiteSpace(l.Host)).ToList());
    }

    /// <summary>"host:port", "http://host:port" or "socks5://host:port" → proxy address; null when not valid.</summary>
    public static Uri? ParseProxy(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "socks5" or "socks4" or "socks4a")) return null;
        if (string.IsNullOrEmpty(uri.Host) || uri.IsDefaultPort && !text.Contains(":" + uri.Port, StringComparison.Ordinal)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath is not ("/" or "") || uri.Query.Length > 0) return null; // login goes in its own fields
        return new Uri($"{uri.Scheme}://{uri.Authority}");
    }

    /// <summary>Applies the proxy and site logins to a new handler.</summary>
    public static void Apply(SocketsHttpHandler handler, bool siteLogins = true)
    {
        var s = _current;
        switch (s.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Custom when s.Proxy != null:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(s.Proxy) { BypassProxyOnLocal = BypassLocal, Credentials = s.ProxyLogin };
                break;
            default:
                handler.UseProxy = true; // system proxy
                break;
        }
        if (siteLogins && s.Logins.Count > 0)
        {
            handler.Credentials = new SiteCredentials(s.Logins);
            handler.PreAuthenticate = true; // after the first accepted login, later requests (other connections) skip the 401 round trip
        }
    }

    /// <summary>Environment for yt-dlp: the custom proxy (with its login, kept off the command line) or none.</summary>
    public static Dictionary<string, string> ToolEnvironment()
    {
        var s = _current;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (s.Mode == ProxyMode.Custom && s.Proxy != null)
        {
            var login = s.ProxyLogin == null ? "" : Uri.EscapeDataString(s.ProxyLogin.UserName) + ":" + Uri.EscapeDataString(s.ProxyLogin.Password) + "@";
            var url = $"{s.Proxy.Scheme}://{login}{s.Proxy.Authority}";
            foreach (var k in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) env[k] = url;
            env["NO_PROXY"] = BypassLocal ? "localhost,127.0.0.1,::1" : "";
        }
        return env;
    }

    /// <summary>yt-dlp argument: "--proxy ''" forces a direct connection; null leaves yt-dlp on the system/env proxy.</summary>
    public static string? ToolProxyArgument() => _current.Mode == ProxyMode.None ? "" : null;

    /// <summary>The login stored for a host (exact host, or a parent domain entry).</summary>
    public static SiteLogin? FindLogin(string host) => FindLogin(_current.Logins, host);

    private static SiteLogin? FindLogin(IReadOnlyList<SiteLogin> logins, string host)
    {
        host = host.ToLowerInvariant();
        return logins
            .Where(l => { var h = NormalizeHost(l.Host); return host == h || host.EndsWith("." + h, StringComparison.Ordinal); })
            .MaxBy(l => NormalizeHost(l.Host).Length); // most specific
    }

    public static string NormalizeHost(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var u)) text = u.Host;
        return text.TrimStart('.').TrimEnd('.');
    }

    /// <summary>
    /// Answers a server's login request with the saved login for that host only. Basic sends the password
    /// readable, so it is only given over https (or to this computer); Digest/NTLM/Negotiate never send it in the clear.
    /// </summary>
    private sealed class SiteCredentials(IReadOnlyList<SiteLogin> logins) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (string.Equals(authType, "Basic", StringComparison.OrdinalIgnoreCase) && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) return null;
            var login = FindLogin(logins, uri.Host);
            if (login == null) return null;
            var password = Dpapi.Unprotect(login.ProtectedPassword);
            return password == null ? null : new NetworkCredential(login.User, password);
        }
    }
}
