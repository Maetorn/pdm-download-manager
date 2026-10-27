using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PDM.Core.Video;

/// <summary>
/// Optional external tools (yt-dlp, FFmpeg, Deno). Nothing is downloaded unless the user asks for it in
/// Settings. Files come only from the projects' official GitHub releases and are checked against the
/// SHA-256 list published in the same release before they are put in place.
/// Deno is the JavaScript runtime yt-dlp uses for YouTube's challenges; yt-dlp runs it sandboxed
/// (no file, network or environment access).
/// </summary>
public sealed class ToolManager
{
    public const string YtDlpRepo = "https://github.com/yt-dlp/yt-dlp";
    public const string FfmpegRepo = "https://github.com/yt-dlp/FFmpeg-Builds";
    private const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl.zip";
    public const string DenoRepo = "https://github.com/denoland/deno";
    private const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip";

    public string Dir { get; }

    public ToolManager(string dir) => Dir = dir;

    public string YtDlpPath => Path.Combine(Dir, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(Dir, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(Dir, "ffprobe.exe");

    public bool HasYtDlp => File.Exists(YtDlpPath);
    public bool HasFfmpeg => File.Exists(FfmpegPath);
    public string DenoPath => Path.Combine(Dir, "deno.exe");
    public bool HasDeno => File.Exists(DenoPath);

    public async Task<string?> GetYtDlpVersionAsync()
    {
        if (!HasYtDlp) return null;
        try
        {
            var psi = new ProcessStartInfo(YtDlpPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            var v = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return v.Trim();
        }
        catch { return null; }
    }

    /// <summary>Downloads (or updates) yt-dlp.exe. Progress is 0–100.</summary>
    public async Task InstallYtDlpAsync(IProgress<double>? progress, CancellationToken ct)
    {
        using var http = NewClient();
        var tag = await LatestTagAsync(http, YtDlpRepo, ct).ConfigureAwait(false);
        var baseUrl = $"{YtDlpRepo}/releases/download/{tag}/";
        var sums = await http.GetStringAsync(baseUrl + "SHA2-256SUMS", ct).ConfigureAwait(false);
        var expected = FindHash(sums, "yt-dlp.exe") ?? throw new InvalidDataException("yt-dlp.exe is not listed in SHA2-256SUMS.");
        var tmp = await DownloadVerifiedAsync(http, baseUrl + "yt-dlp.exe", expected, progress, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Dir);
        File.Move(tmp, YtDlpPath, overwrite: true);
        Log.Info($"yt-dlp {tag} installed (sha256 verified)");
    }

    /// <summary>Downloads FFmpeg (yt-dlp's Windows build) and keeps ffmpeg.exe and ffprobe.exe.</summary>
    public async Task InstallFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        using var http = NewClient();
        var baseUrl = $"{FfmpegRepo}/releases/download/latest/";
        var sums = await http.GetStringAsync(baseUrl + "checksums.sha256", ct).ConfigureAwait(false);
        var expected = FindHash(sums, FfmpegAsset) ?? throw new InvalidDataException("FFmpeg build is not listed in checksums.sha256.");
        var zip = await DownloadVerifiedAsync(http, baseUrl + FfmpegAsset, expected, progress, ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Dir);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var wanted in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                // Only these two entries are extracted, to fixed paths (no archive paths are used).
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(wanted + " not found in the archive.");
                var target = Path.Combine(Dir, wanted);
                entry.ExtractToFile(target + ".new", overwrite: true);
                File.Move(target + ".new", target, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
        Log.Info("FFmpeg installed (sha256 verified)");
    }

    /// <summary>Downloads Deno (official release) and keeps deno.exe.</summary>
    public async Task InstallDenoAsync(IProgress<double>? progress, CancellationToken ct)
    {
        using var http = NewClient();
        var tag = await LatestTagAsync(http, DenoRepo, ct).ConfigureAwait(false);
        var baseUrl = $"{DenoRepo}/releases/download/{tag}/";
        var sums = await http.GetStringAsync(baseUrl + DenoAsset + ".sha256sum", ct).ConfigureAwait(false);
        var expected = FindHash(sums, DenoAsset) ?? throw new InvalidDataException("Deno's checksum file does not list " + DenoAsset + ".");
        var zip = await DownloadVerifiedAsync(http, baseUrl + DenoAsset, expected, progress, ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Dir);
            using var archive = ZipFile.OpenRead(zip);
            // Only deno.exe is taken, to a fixed path.
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, "deno.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("deno.exe not found in the archive.");
            entry.ExtractToFile(DenoPath + ".new", overwrite: true);
            File.Move(DenoPath + ".new", DenoPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
        Log.Info($"Deno {tag} installed (sha256 verified)");
    }

    /// <summary>"deno 2.9.8 (stable, release, x86_64-pc-windows-msvc)" → "2.9.8".</summary>
    public async Task<string?> GetDenoVersionAsync()
    {
        if (!HasDeno) return null;
        try
        {
            var psi = new ProcessStartInfo(DenoPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            var v = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return ParseDenoVersion(v);
        }
        catch { return null; }
    }

    public static string? ParseDenoVersion(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"^deno (\d+\.\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// With yt-dlp installed, Deno is installed too (older installs got it later) and kept current.
    /// Returns the new version, or null when nothing changed.
    /// </summary>
    public async Task<string?> UpdateDenoIfNeededAsync(CancellationToken ct)
    {
        if (!HasYtDlp) return null;
        using var http = NewClient();
        var latest = await LatestTagAsync(http, DenoRepo, ct).ConfigureAwait(false);
        var current = await GetDenoVersionAsync().ConfigureAwait(false);
        if (current != null && string.Equals("v" + current, latest, StringComparison.Ordinal)) return null;
        await InstallDenoAsync(null, ct).ConfigureAwait(false);
        return latest;
    }

    /// <summary>
    /// Sites like YouTube change often and an old yt-dlp stops working, so the installed copy is kept current:
    /// installs the latest release (SHA-256 checked as usual) when its tag differs from the installed version.
    /// Returns the new version, or null when nothing changed.
    /// </summary>
    public async Task<string?> UpdateYtDlpIfOutdatedAsync(CancellationToken ct)
    {
        if (!HasYtDlp) return null;
        var current = await GetYtDlpVersionAsync().ConfigureAwait(false);
        using var http = NewClient();
        var latest = await LatestTagAsync(http, YtDlpRepo, ct).ConfigureAwait(false);
        if (current != null && string.Equals(current, latest, StringComparison.Ordinal)) return null;
        await InstallYtDlpAsync(null, ct).ConfigureAwait(false);
        return latest;
    }

    public void Uninstall()
    {
        foreach (var p in new[] { YtDlpPath, FfmpegPath, FfprobePath, DenoPath })
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    private static HttpClient NewClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
        PDM.Core.Engine.NetConfig.Apply(handler, siteLogins: false);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PDM/" + AppPaths.Version);
        return http;
    }

    /// <summary>Resolves "latest" to a concrete tag so the checksum list and binary come from the same release.</summary>
    private static async Task<string> LatestTagAsync(HttpClient http, string repo, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        PDM.Core.Engine.NetConfig.Apply(handler, siteLogins: false);
        using var noRedirect = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("PDM/" + AppPaths.Version);
        using var resp = await noRedirect.GetAsync(repo + "/releases/latest", ct).ConfigureAwait(false);
        var loc = resp.Headers.Location?.ToString() ?? throw new InvalidDataException("Could not find the latest release.");
        var tag = loc.TrimEnd('/').Split('/').Last();
        if (tag.Length == 0 || tag.Length > 64 || tag.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new InvalidDataException("Unexpected release tag.");
        return tag;
    }

    /// <summary>
    /// SHA-256 for <paramref name="fileName"/> from a checksum list: "hash  name" lines (sha256sum), or PowerShell's
    /// "Hash : …" / "Path : …\name" (Deno's Windows files). The name must match, so a list for another file is refused.
    /// </summary>
    public static string? FindHash(string sums, string fileName)
    {
        string? psHash = null;
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == fileName && IsSha256(parts[0]))
                return parts[0].ToLowerInvariant();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key == "Hash" && IsSha256(value)) psHash = value.ToLowerInvariant();
            else if (key == "Path" && psHash != null)
            {
                if (value.Replace('\\', '/').Split('/').Last() == fileName) return psHash;
                psHash = null; // that hash belongs to another file
            }
        }
        return null;
    }

    private static bool IsSha256(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    private async Task<string> DownloadVerifiedAsync(HttpClient http, string url, string expectedSha256, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(done * 100.0 / total);
                }
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expectedSha256)))
                throw new InvalidDataException("SHA-256 mismatch — the file was not installed.");
            return tmp;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
