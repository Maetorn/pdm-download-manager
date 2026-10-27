using System.Text.Json.Serialization;

namespace PDM.Core.Models;

public enum PostQueueAction
{
    None,
    ExitApp,
    Sleep,
    Shutdown,
}

/// <summary>A named download queue with its own schedule (like IDM's main queue and custom queues).</summary>
public sealed class QueueInfo
{
    public const string MainId = "main";

    public string Id { get; set; } = MainId;
    /// <summary>Shown name; empty for the main queue (the UI shows its translated name).</summary>
    public string Name { get; set; } = "";
    public bool ScheduleEnabled { get; set; }
    /// <summary>Next start time; moves on by a day each time it passes.</summary>
    public DateTime? StartAt { get; set; }
    public DateTime? StopAt { get; set; }
    /// <summary>Days the schedule runs (0 = Sunday … 6 = Saturday); empty = every day.</summary>
    public List<int> Days { get; set; } = new();
    /// <summary>Downloads of this queue at the same time; 0 = the general limit.</summary>
    public int MaxConcurrent { get; set; }
    /// <summary>What to do when this queue has nothing left to download.</summary>
    public PostQueueAction PostAction { get; set; } = PostQueueAction.None;

    public bool RunsOn(DayOfWeek day) => Days.Count == 0 || Days.Contains((int)day);
}

public sealed class AppSettings
{
    /// <summary>Settings format; Normalize() upgrades older files. 0 = written before versioning (or new).</summary>
    public int Version { get; set; }
    public const int CurrentVersion = 3;

    // ---- General ----
    /// <summary>"auto" (Windows display language) or en, tr, de, fr, it.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>System / Light / Dark.</summary>
    public string Theme { get; set; } = "System";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;
    public bool ClipboardMonitor { get; set; } = true;
    /// <summary>Keep Windows from sleeping while something downloads.</summary>
    public bool PreventSleep { get; set; } = true;
    /// <summary>Try failed downloads again by themselves when the connection or the server comes back.</summary>
    public bool AutoRetry { get; set; } = true;
    /// <summary>Show the IDM-style progress window when a single download starts.</summary>
    public bool ShowProgressWindow { get; set; } = true;
    /// <summary>Show the "Download complete" window (Open / Open with / Open folder) when such a download finishes.</summary>
    public bool ShowCompleteDialog { get; set; } = true;

    // ---- Files ----
    public string DownloadFolder { get; set; } = DefaultDownloadFolder();
    /// <summary>Like IDM: Downloads\Compressed, Documents, Music, Programs, Video (other files stay in the folder itself).</summary>
    public bool UseCategoryFolders { get; set; } = true;
    /// <summary>When the target exists: true = add " (1)", false = ask in the add dialog (auto-rename for silent adds).</summary>
    public bool AutoRenameDuplicates { get; set; } = true;

    /// <summary>Extract finished zip/7z/rar/tar archives into a folder next to them.</summary>
    public bool AutoExtract { get; set; }
    /// <summary>After a successful extraction, move the archive to the Recycle Bin.</summary>
    public bool DeleteArchiveAfterExtract { get; set; }

    // ---- Connection ----
    public int DefaultConnections { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    /// <summary>Global speed limit in KB/s; 0 = unlimited.</summary>
    public int SpeedLimitKBps { get; set; }
    /// <summary>Value restored by the quick speed-limit switch.</summary>
    public int LastSpeedLimitKBps { get; set; } = 1024;
    public int MaxRetries { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 30;
    public string CustomUserAgent { get; set; } = "";
    public PDM.Core.Engine.ProxyMode ProxyMode { get; set; } = PDM.Core.Engine.ProxyMode.System;
    /// <summary>"host:port", "http://host:port" or "socks5://host:port".</summary>
    public string ProxyAddress { get; set; } = "";
    public string ProxyUser { get; set; } = "";
    /// <summary>DPAPI-encrypted (current user).</summary>
    public string? ProtectedProxyPassword { get; set; }
    /// <summary>Usernames/passwords for sites that ask for one (HTTP Basic/Digest).</summary>
    public List<PDM.Core.Engine.SiteLogin> Logins { get; set; } = new();

    // ---- Browser ----
    /// <summary>Show the "download info" dialog for downloads handed over by the browser.</summary>
    public bool ShowDialogForBrowserDownloads { get; set; } = true;
    /// <summary>Use cookies the extension sends with a captured download (kept DPAPI-encrypted, deleted when done).</summary>
    public bool AcceptBrowserCookies { get; set; } = true;
    /// <summary>Extra extension origins allowed to talk to the app (development only).</summary>
    public List<string> ExtraAllowedOrigins { get; set; } = new();

    // ---- Security ----
    /// <summary>Hand finished files to Windows Attachment Services (Mark-of-the-Web + antivirus scan).</summary>
    public bool ScanWithAttachmentServices { get; set; } = true;
    public bool WarnBeforeOpeningExecutables { get; set; } = true;

    // ---- Video ----
    public string PreferredVideoQuality { get; set; } = "1080";
    public string PreferredContainer { get; set; } = "mp4";

    // ---- Updates ----
    /// <summary>Install new versions from the project's GitHub releases automatically (when no download is running).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Old: GitHub token from when the repository was private. Read only so Normalize() can drop it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ProtectedUpdateToken { get; set; }

    // ---- Queues ----
    /// <summary>The main queue is always first.</summary>
    public List<QueueInfo> Queues { get; set; } = new();

    // Single-queue scheduler of settings v2; read once and moved into the main queue.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SchedulerEnabled { get; set; }
    public DateTime? SchedulerStartAt { get; set; }
    public DateTime? SchedulerStopAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PostQueueAction PostQueueAction { get; set; } = PostQueueAction.None;

    public QueueInfo MainQueue => Queues[0];

    public QueueInfo? FindQueue(string? id) => id == null ? null : Queues.FirstOrDefault(q => q.Id == id);

    public static string DefaultDownloadFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads");
    }

    public void Normalize()
    {
        if (Version < 3)
        {
            // Category subfolders are on by default (IDM layout); older files had them off without a choice.
            // (v2 meant to do this, but files without a version number were read as current and skipped it.)
            UseCategoryFolders = true;
            Queues ??= new();
            if (!Queues.Any(q => q.Id == QueueInfo.MainId))
            {
                Queues.Insert(0, new QueueInfo
                {
                    ScheduleEnabled = SchedulerEnabled,
                    StartAt = SchedulerStartAt,
                    StopAt = SchedulerStopAt,
                    PostAction = PostQueueAction,
                });
            }
        }
        Version = CurrentVersion;
        SchedulerEnabled = false;
        SchedulerStartAt = SchedulerStopAt = null;
        PostQueueAction = PostQueueAction.None;
        NormalizeQueues();
        DefaultConnections = Math.Clamp(DefaultConnections, 1, 16);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 10);
        SpeedLimitKBps = Math.Max(0, SpeedLimitKBps);
        MaxRetries = Math.Clamp(MaxRetries, 0, 100);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        ProtectedUpdateToken = null; // updates need no token any more; do not keep a secret we no longer use
        if (Language is not ("auto" or "en" or "tr" or "de" or "fr" or "it")) Language = "auto";
        if (Theme is not ("System" or "Light" or "Dark")) Theme = "System";
        if (string.IsNullOrWhiteSpace(DownloadFolder)) DownloadFolder = DefaultDownloadFolder();
        ExtraAllowedOrigins ??= new();
        Logins ??= new();
        Logins.RemoveAll(l => l == null || string.IsNullOrWhiteSpace(l.Host));
        ProxyAddress = (ProxyAddress ?? "").Trim();
        ProxyUser ??= "";
    }

    private void NormalizeQueues()
    {
        Queues ??= new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Queues.RemoveAll(q => q == null || string.IsNullOrWhiteSpace(q.Id) || !seen.Add(q.Id));
        var main = Queues.FirstOrDefault(q => q.Id == QueueInfo.MainId) ?? new QueueInfo();
        Queues.Remove(main);
        Queues.Insert(0, main);
        if (Queues.Count > 20) Queues.RemoveRange(20, Queues.Count - 20);
        foreach (var q in Queues)
        {
            q.Name = (q.Name ?? "").Trim();
            if (q.Name.Length > 40) q.Name = q.Name[..40];
            q.Days = (q.Days ?? new()).Where(d => d is >= 0 and <= 6).Distinct().Order().ToList();
            if (q.Days.Count == 7) q.Days.Clear();
            q.MaxConcurrent = Math.Clamp(q.MaxConcurrent, 0, 10);
            if (q.StartAt == null && q.StopAt == null) q.ScheduleEnabled = false;
        }
    }
}
