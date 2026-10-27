namespace PDM.Core;

/// <summary>Free space checks before writing large files.</summary>
public static class DiskSpace
{
    /// <summary>Kept free on top of the file itself.</summary>
    public const long Reserve = 32L * 1024 * 1024;

    /// <summary>Free bytes on the drive of <paramref name="path"/>, or -1 when it cannot be told (network share, no access).</summary>
    public static long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;
            var d = new DriveInfo(root);
            return d.IsReady ? d.AvailableFreeSpace : -1;
        }
        catch { return -1; }
    }

    public static bool HasRoomFor(string path, long bytes)
    {
        if (bytes <= 0) return true;
        var free = FreeBytes(path);
        return free < 0 || free >= bytes + Reserve;
    }

    public static bool IsDiskFull(Exception ex) => NativeFile.IsDiskFull(ex);

    /// <summary>Thrown before a download starts; the engine reports it as "disk full".</summary>
    public static IOException NotEnoughSpace() => new("Not enough disk space.", unchecked((int)0x80070070));
}
