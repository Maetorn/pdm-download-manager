namespace PDM.Core.Models;

public enum FileCategory
{
    Other,
    Compressed,
    Document,
    Music,
    Program,
    Video,
    Image,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    /// <summary>Extensions that run code when opened. Used for warnings, never for blocking.</summary>
    public static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".com", ".scr", ".pif",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".scf",
        ".reg", ".cpl", ".jar", ".dll", ".sys", ".inf", ".msp", ".application", ".appref-ms", ".gadget", ".iso", ".img", ".vhd", ".vhdx",
        // Also run code or open something that does: consoles, help files, shell links, Office add-ins, installers, scripts.
        ".msc", ".chm", ".library-ms", ".settingcontent-ms", ".search-ms", ".xll", ".wsc", ".sct", ".ps1xml", ".psd1", ".psc1",
        ".mst", ".msu", ".appinstaller", ".diagcab", ".py", ".pyw", ".vb", ".ws", ".jnlp", ".mshxml", ".theme", ".themepack",
        ".desktopthemepack", ".cab", ".xbap",
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) d["." + e] = c; }
        Add(FileCategory.Compressed, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "lzh", "arj", "z");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "csv", "epub", "md");
        Add(FileCategory.Music, "mp3", "m4a", "aac", "flac", "wav", "ogg", "opus", "wma", "aiff", "ape");
        Add(FileCategory.Program, "exe", "msi", "msix", "msixbundle", "appx", "apk", "iso", "img", "dmg", "deb", "rpm", "bin", "jar");
        Add(FileCategory.Video, "mp4", "mkv", "webm", "avi", "mov", "wmv", "flv", "m4v", "mpg", "mpeg", "3gp", "ts", "m2ts");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "tif", "tiff", "heic", "avif", "ico");
        return d;
    }

    public static FileCategory Of(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return FileCategory.Other;
        return Map.TryGetValue(Path.GetExtension(fileName), out var c) ? c : FileCategory.Other;
    }

    public static bool IsExecutable(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && Executable.Contains(Path.GetExtension(fileName));

    /// <summary>Folder name used when "category subfolders" is on.</summary>
    public static string FolderName(FileCategory c) => c switch
    {
        FileCategory.Compressed => "Compressed",
        FileCategory.Document => "Documents",
        FileCategory.Music => "Music",
        FileCategory.Program => "Programs",
        FileCategory.Video => "Video",
        _ => "", // images and everything else stay in the download folder itself (as in IDM)
    };
}
