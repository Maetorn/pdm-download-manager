using PDM.Core.Models;

namespace PDM.Core;

public sealed class HistoryEntry
{
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public long Size { get; set; }
    public DateTime CompletedAt { get; set; }
    public string? Referrer { get; set; }
    public DownloadKind Kind { get; set; }
    /// <summary>Video choices, so "download again" asks for the same quality.</summary>
    public VideoOptions? Video { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FullPath => Path.Combine(Folder, FileName);
}

public sealed class DayStat
{
    public long Bytes { get; set; }
    public int Files { get; set; }
}

public sealed class HistoryData
{
    /// <summary>Newest first.</summary>
    public List<HistoryEntry> Entries { get; set; } = new();
    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    /// <summary>"yyyy-MM-dd" → that day's finished downloads.</summary>
    public Dictionary<string, DayStat> Days { get; set; } = new();
}

/// <summary>
/// Finished downloads, kept even after they are removed from the list, plus totals per day.
/// Loaded on first use and written in the background, so it never slows a download down.
/// </summary>
public sealed class History
{
    public const int MaxEntries = 5000;
    public const int MaxDays = 400;

    private readonly object _lock = new();
    private HistoryData? _data;
    private bool _saving;
    private bool _dirty;

    public static string FilePath => Path.Combine(AppPaths.DataDir, "history.json");

    private HistoryData Data => _data ??= Store.LoadHistory() ?? new HistoryData();

    public void Record(DownloadItem item)
    {
        var size = item.TotalBytes > 0 ? item.TotalBytes : item.DownloadedBytes;
        var at = item.CompletedAt ?? DateTime.Now;
        lock (_lock)
        {
            var d = Data;
            d.Entries.Insert(0, new HistoryEntry
            {
                Url = item.Url,
                FileName = item.FileName,
                Folder = item.Folder,
                Size = size,
                CompletedAt = at,
                Referrer = item.Referrer,
                Kind = item.Kind,
                Video = item.Video,
            });
            if (d.Entries.Count > MaxEntries) d.Entries.RemoveRange(MaxEntries, d.Entries.Count - MaxEntries);
            d.TotalBytes += Math.Max(0, size);
            d.TotalFiles++;
            var key = at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (!d.Days.TryGetValue(key, out var day)) d.Days[key] = day = new DayStat();
            day.Bytes += Math.Max(0, size);
            day.Files++;
            if (d.Days.Count > MaxDays)
                foreach (var old in d.Days.Keys.Order(StringComparer.Ordinal).Take(d.Days.Count - MaxDays).ToList()) d.Days.Remove(old);
        }
        SaveSoon();
    }

    /// <summary>A copy for the UI.</summary>
    public HistoryData Snapshot()
    {
        lock (_lock)
        {
            var d = Data;
            return new HistoryData
            {
                Entries = d.Entries.ToList(),
                TotalBytes = d.TotalBytes,
                TotalFiles = d.TotalFiles,
                Days = d.Days.ToDictionary(kv => kv.Key, kv => new DayStat { Bytes = kv.Value.Bytes, Files = kv.Value.Files }),
            };
        }
    }

    /// <summary>Forgets the file list and/or the totals.</summary>
    public void Clear(bool entries, bool stats)
    {
        lock (_lock)
        {
            if (entries) Data.Entries.Clear();
            if (stats)
            {
                Data.TotalBytes = 0;
                Data.TotalFiles = 0;
                Data.Days.Clear();
            }
        }
        SaveSoon();
    }

    public void Remove(IEnumerable<HistoryEntry> entries)
    {
        var set = entries.Select(e => (e.Url, e.CompletedAt, e.FileName)).ToHashSet();
        lock (_lock) Data.Entries.RemoveAll(e => set.Contains((e.Url, e.CompletedAt, e.FileName)));
        SaveSoon();
    }

    /// <summary>Writes now (app exit, tests).</summary>
    public void Flush()
    {
        HistoryData copy;
        lock (_lock)
        {
            if (_data == null || !_dirty) return;
            _dirty = false;
            copy = Snapshot();
        }
        try { Store.SaveHistory(copy); } catch (Exception ex) { Log.Error("Saving history failed", ex); }
    }

    private void SaveSoon()
    {
        lock (_lock)
        {
            _dirty = true;
            if (_saving) return;
            _saving = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false); // several downloads finishing together → one write
            lock (_lock) _saving = false;
            Flush();
        });
    }
}
