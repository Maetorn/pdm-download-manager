using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PDM.Core.Update;

/// <summary>A newer build published as a GitHub release of this project.</summary>
public sealed record UpdateInfo(string Version, string ExeAssetUrl, string ShaAssetUrl, long Size, string? Notes);

/// <summary>
/// Looks for new versions in the project's public GitHub releases (no login or token) and downloads PDM.exe.
/// The download must match the SHA-256 published next to it. Requests go over https to api.github.com, which
/// redirects asset downloads to GitHub's storage host.
/// </summary>
public static class UpdateClient
{
    public const string Repository = "Maetorn/pdm-download-manager";
    public const string ExeAsset = "PDM.exe";
    public const string ShaAsset = "PDM.exe.sha256";
    private const long MaxExeBytes = 400L * 1024 * 1024;

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate.TrimStart('v', 'V'), out var a) && Version.TryParse(current.TrimStart('v', 'V'), out var b) && a > b;

    /// <summary>Reads the latest-release JSON. Returns null when it has no PDM.exe + checksum pair.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out _)) return null;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        string? exe = null, sha = null;
        long size = -1;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var url = a.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (url == null || !url.StartsWith("https://api.github.com/repos/" + Repository + "/releases/assets/", StringComparison.Ordinal)) continue;
                if (name == ExeAsset)
                {
                    exe = url;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : -1;
                }
                else if (name == ShaAsset) sha = url;
            }
        }
        if (exe == null || sha == null) return null;
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        return new UpdateInfo(tag.TrimStart('v', 'V'), exe, sha, size, notes is { Length: > 500 } ? notes[..500] : notes);
    }

    /// <summary>"&lt;64 hex&gt;  PDM.exe" (sha256sum format) or just the hash.</summary>
    public static string? ParseSha256(string text)
    {
        var first = text.Trim().Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first : null;
    }

    private static HttpClient NewClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        PDM.Core.Engine.NetConfig.Apply(handler, siteLogins: false);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PDM/" + AppPaths.Version);
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>Newer release than <paramref name="currentVersion"/>, or null. Throws HttpRequestException (403/429 = GitHub's hourly limit).</summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct)
    {
        using var http = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        resp.EnsureSuccessStatusCode();
        var info = ParseRelease(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return info != null && IsNewer(info.Version, currentVersion) ? info : null;
    }

    /// <summary>Downloads PDM.exe to <paramref name="targetPath"/> and checks its SHA-256. Deletes the file on any failure.</summary>
    public static async Task DownloadAsync(UpdateInfo info, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = NewClient();
        var expected = ParseSha256(await GetAssetStringAsync(http, info.ShaAssetUrl, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("Checksum file is not valid.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.ExeAssetUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            if (total > MaxExeBytes) throw new InvalidDataException("Update is too large.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += n;
                    if (done > MaxExeBytes) throw new InvalidDataException("Update is too large.");
                    sha.AppendData(buffer, 0, n);
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (total > 0) progress?.Report(done * 100.0 / total);
                }
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (actual != expected) throw new InvalidDataException("SHA-256 of the update does not match.");
        }
        catch
        {
            try { File.Delete(targetPath); } catch { }
            throw;
        }
    }

    private static async Task<string> GetAssetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
