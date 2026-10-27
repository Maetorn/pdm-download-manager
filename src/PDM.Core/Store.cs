using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PDM.Core.Models;

namespace PDM.Core;

/// <summary>JSON files with atomic replace and a .bak copy, so a crash mid-write never loses the list.</summary>
public static class Store
{
    private static readonly object Gate = new();

    public static string SettingsPath => Path.Combine(AppPaths.DataDir, "settings.json");
    public static string DownloadsPath => Path.Combine(AppPaths.DataDir, "downloads.json");

    public static AppSettings LoadSettings()
    {
        var s = Load(SettingsPath, PdmJson.Default.AppSettings) ?? new AppSettings();
        s.Normalize();
        return s;
    }

    public static void SaveSettings(AppSettings settings) => Save(SettingsPath, settings, PdmJson.Default.AppSettings);

    public static List<DownloadItem> LoadDownloads() => Load(DownloadsPath, PdmJson.Default.ListDownloadItem) ?? new List<DownloadItem>();

    public static void SaveDownloads(List<DownloadItem> items) => Save(DownloadsPath, items, PdmJson.Default.ListDownloadItem);

    public static HistoryData? LoadHistory() => Load(History.FilePath, PdmJson.Default.HistoryData);

    public static void SaveHistory(HistoryData data) => Save(History.FilePath, data, PdmJson.Default.HistoryData);

    private static T? Load<T>(string path, JsonTypeInfo<T> info) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                using var fs = File.OpenRead(p);
                var value = JsonSerializer.Deserialize(fs, info);
                if (value != null) return value;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {Path.GetFileName(p)}: {ex.GetType().Name}");
            }
        }
        return null;
    }

    private static void Save<T>(string path, T value, JsonTypeInfo<T> info)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fs, value, info);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); return; }
                catch (IOException) { }
            }
            File.Move(tmp, path, overwrite: true);
        }
    }
}
