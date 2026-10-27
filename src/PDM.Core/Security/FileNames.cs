using System.Net.Http.Headers;
using System.Text;

namespace PDM.Core.Security;

/// <summary>Turns untrusted names (from URLs, headers, the browser) into safe Windows file names.</summary>
public static class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public const int MaxLength = 180;

    /// <summary>
    /// Removes path parts, characters Windows forbids, control and bidi-override characters (used to disguise
    /// "evil‮fdp.exe" as "evilexe.pdf"), reserved device names and trailing dots/spaces.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        // Keep only the last path component.
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < 32 || ch == 127) continue;
            if (IsBidiOrInvisible(ch)) continue;
            sb.Append(ch switch
            {
                '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' => '_',
                _ => ch,
            });
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        while (result.StartsWith('.')) result = result[1..]; // no hidden "dot files" / ".." tricks
        if (result.Length == 0) return fallback;

        var dot = result.IndexOf('.');
        var stem = dot >= 0 ? result[..dot] : result;
        if (Reserved.Contains(stem.TrimEnd(' '))) result = "_" + result;

        if (result.Length > MaxLength)
        {
            var ext = Path.GetExtension(result);
            if (ext.Length > 20) ext = "";
            result = result[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return result;
    }

    private static bool IsBidiOrInvisible(char ch) =>
        ch is '​' or '‌' or '‍' or '‎' or '‏' or '⁠' or '﻿'
        || (ch >= '‪' && ch <= '‮')
        || (ch >= '⁦' && ch <= '⁩');

    /// <summary>Full path of <paramref name="fileName"/> inside <paramref name="folder"/>; throws if it would escape the folder.</summary>
    public static string CombineInside(string folder, string fileName)
    {
        var root = Path.GetFullPath(folder);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length)
            throw new InvalidOperationException("Invalid file name.");
        return full;
    }

    /// <summary>Returns a name that does not exist in the folder yet ("a.zip" → "a (1).zip").</summary>
    public static string MakeUnique(string folder, string fileName, Func<string, bool>? isTaken = null)
    {
        bool Taken(string n)
        {
            var p = Path.Combine(folder, n);
            return File.Exists(p) || File.Exists(p + ".pdmdl") || Directory.Exists(p) || (isTaken?.Invoke(p) ?? false);
        }
        if (!Taken(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    /// <summary>File name from a Content-Disposition header value, or null.</summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        if (!ContentDispositionHeaderValue.TryParse(header, out var cd))
        {
            // Some servers send unquoted names with spaces; fall back to a simple scan.
            var i = header.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var raw = header[(i + 9)..].Split(';')[0].Trim().Trim('"');
            return FixEncoding(raw);
        }
        var star = cd.FileNameStar;
        if (!string.IsNullOrWhiteSpace(star)) return star;
        var name = cd.FileName?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : FixEncoding(name);
    }

    /// <summary>Repairs names that were sent as raw UTF-8 or percent-encoded.</summary>
    private static string FixEncoding(string name)
    {
        if (name.Contains('%'))
        {
            try { name = Uri.UnescapeDataString(name); } catch { }
        }
        if (name.Any(c => c >= 0x80 && c <= 0xFF) && name.All(c => c <= 0xFF))
        {
            try
            {
                var bytes = Encoding.Latin1.GetBytes(name);
                var utf8 = new UTF8Encoding(false, true).GetString(bytes);
                return utf8;
            }
            catch (DecoderFallbackException) { }
        }
        return name;
    }

    /// <summary>Last path segment of a URL, decoded, or null.</summary>
    public static string? FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var seg = uri.AbsolutePath.TrimEnd('/');
        var i = seg.LastIndexOf('/');
        seg = i >= 0 ? seg[(i + 1)..] : seg;
        if (seg.Length == 0) return null;
        try { seg = Uri.UnescapeDataString(seg); } catch { }
        return seg;
    }

    /// <summary>Adds an extension from the MIME type when the name has none.</summary>
    public static string EnsureExtension(string fileName, string? contentType)
    {
        if (Path.HasExtension(fileName) || string.IsNullOrEmpty(contentType)) return fileName;
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        var ext = mime switch
        {
            "application/pdf" => ".pdf",
            "application/zip" or "application/x-zip-compressed" => ".zip",
            "application/x-7z-compressed" => ".7z",
            "application/vnd.rar" or "application/x-rar-compressed" => ".rar",
            "application/x-msdownload" or "application/vnd.microsoft.portable-executable" => ".exe",
            "application/x-msi" => ".msi",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "text/plain" => ".txt",
            "application/json" => ".json",
            "application/gzip" or "application/x-gzip" => ".gz",
            _ => "",
        };
        return fileName + ext;
    }

    /// <summary>URL without user info or query, for logs.</summary>
    public static string Redact(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "(invalid url)";
        return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{u.AbsolutePath}{(string.IsNullOrEmpty(u.Query) ? "" : "?…")}";
    }

    /// <summary>Every http(s) address inside a text (e.g. a tool's error message) redacted like <see cref="Redact"/>.</summary>
    public static string RedactUrlsIn(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : System.Text.RegularExpressions.Regex.Replace(text, @"https?://[^\s""'<>]+", m => Redact(m.Value));

    /// <summary>URL without user info (for Zone.Identifier HostUrl).</summary>
    public static string WithoutUserInfo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo)) return url;
        var b = new UriBuilder(u) { UserName = "", Password = "" };
        return b.Uri.AbsoluteUri;
    }

    public static bool IsHttpUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 16384) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(u.Host)) return false;
        uri = u;
        return true;
    }
}
