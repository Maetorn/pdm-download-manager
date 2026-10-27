using System.Diagnostics;
using System.IO.Compression;
using PDM.Core.Security;

namespace PDM.Core;

/// <summary>
/// Extracts a finished archive into a new folder next to it. Zip uses .NET; 7z, rar and tar use Windows' own
/// tar.exe (libarchive), which refuses absolute paths, ".." and links that point outside by default.
/// Nothing that comes out is ever run; extracted files get the archive's "downloaded from the internet" mark.
/// </summary>
public static class Archives
{
    private static readonly string[] Extensions = [".zip", ".7z", ".rar", ".tar", ".tgz", ".tbz2", ".txz", ".tar.gz", ".tar.bz2", ".tar.xz"];

    /// <summary>Stop extracting when the drive gets this close to full (archives can claim less than they hold).</summary>
    private const long MinFreeWhileExtracting = 256L * 1024 * 1024;

    /// <summary>tar.exe from System32 (Windows 10 1803+); tests on other systems use the system tar.</summary>
    public static string TarPath { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "tar.exe")
        : "/usr/bin/tar";

    public static bool IsArchive(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (!Extensions.Any(e => name.EndsWith(e, StringComparison.Ordinal))) return false;
        // Multi-volume RAR: only the first volume is opened (the others belong to it).
        var m = System.Text.RegularExpressions.Regex.Match(name, @"\.part(\d+)\.rar$");
        return !m.Success || int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>"Downloads\file.zip" → "Downloads\file" (or "file (1)" when taken).</summary>
    public static string TargetFolder(string archivePath)
    {
        var dir = Path.GetDirectoryName(archivePath)!;
        var name = Path.GetFileName(archivePath);
        var lower = name.ToLowerInvariant();
        var ext = Extensions.Where(e => lower.EndsWith(e, StringComparison.Ordinal)).MaxBy(e => e.Length) ?? Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var part = System.Text.RegularExpressions.Regex.Match(stem, @"\.part\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (part.Success) stem = stem[..part.Index];
        stem = FileNames.Sanitize(stem.TrimEnd('.', ' '), "archive");
        var path = Path.Combine(dir, stem);
        for (var i = 1; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i})");
        return path;
    }

    /// <summary>Extracts and returns the new folder. On failure the half-made folder is removed and the archive kept.</summary>
    public static async Task<string> ExtractAsync(string archivePath, CancellationToken ct = default)
    {
        var dest = TargetFolder(archivePath);
        Directory.CreateDirectory(dest);
        try
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) await Task.Run(() => ExtractZip(archivePath, dest, ct), ct).ConfigureAwait(false);
            else await ExtractWithTarAsync(archivePath, dest, ct).ConfigureAwait(false);
            CopyZoneIdentifier(archivePath, dest);
            return dest;
        }
        catch
        {
            try { Directory.Delete(dest, true); } catch { }
            throw;
        }
    }

    private static void ExtractZip(string path, string dest, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (!DiskSpace.HasRoomFor(dest, zip.Entries.Sum(e => e.Length))) throw DiskSpace.NotEnoughSpace();
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var parts = entry.FullName.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
            if (parts.Contains("..") || entry.FullName.StartsWith('/') || entry.FullName.StartsWith('\\') || entry.FullName.Contains(':'))
                throw new InvalidDataException("Archive entry points outside the folder.");
            if (parts.Count == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts.Select(p => FileNames.Sanitize(p)).ToArray())));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive entry points outside the folder.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var src = entry.Open())
            using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                long written = 0;
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    // Never write more than the archive declared (zip bombs lie about sizes).
                    if (written > entry.Length) throw new InvalidDataException("Archive entry is larger than declared.");
                    dst.Write(buffer, 0, n);
                }
            }
            try { File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime); } catch { }
        }
    }

    private static async Task ExtractWithTarAsync(string path, string dest, CancellationToken ct)
    {
        if (!File.Exists(TarPath)) throw new NotSupportedException("tar.exe was not found (Windows 10 1803 or newer is needed for 7z/rar).");
        if (!DiskSpace.HasRoomFor(dest, new FileInfo(path).Length)) throw DiskSpace.NotEnoughSpace();
        var psi = new ProcessStartInfo(TarPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = dest,
        };
        foreach (var a in new[] { "-x", "-f", path, "-C", dest }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("tar did not start.");
        using var job = ToolJob.Contain(p, singleProcess: true); // tar reads an untrusted archive: no other programs
        var stderr = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        var diskFull = false;
        try
        {
            while (!p.HasExited)
            {
                await Task.WhenAny(p.WaitForExitAsync(ct), Task.Delay(500, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var free = DiskSpace.FreeBytes(dest);
                if (free >= 0 && free < MinFreeWhileExtracting)
                {
                    diskFull = true;
                    break;
                }
            }
        }
        finally
        {
            if (!p.HasExited) try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }
        if (diskFull) throw DiskSpace.NotEnoughSpace();
        if (p.ExitCode != 0)
        {
            var err = (await stderr.ConfigureAwait(false)).Trim();
            var line = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
            throw new InvalidDataException("tar exit " + p.ExitCode + (line.Length > 0 ? ": " + (line.Length > 200 ? line[..200] : line) : ""));
        }
    }

    /// <summary>Gives every extracted file the archive's Mark-of-the-Web, as Explorer does.</summary>
    private static void CopyZoneIdentifier(string archivePath, string dest)
    {
        if (!OperatingSystem.IsWindows()) return;
        string zone;
        try { zone = File.ReadAllText(archivePath + ":Zone.Identifier"); }
        catch { return; } // no mark on the archive
        foreach (var file in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
        {
            try { File.WriteAllText(file + ":Zone.Identifier", zone); } catch { }
        }
    }
}
