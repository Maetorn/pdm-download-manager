using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PDM.Core.Models;

public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Error,
    Processing,
}

public enum DownloadKind
{
    Http,
    Video,
}

/// <summary>One byte range of the target file. End is inclusive; Position is the next byte to write.</summary>
public sealed class SegmentState
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Position { get; set; }

    [JsonIgnore] public bool IsDone => Position > End;
    [JsonIgnore] public long Remaining => Math.Max(0, End - Position + 1);
}

/// <summary>Options for a video job handled by yt-dlp.</summary>
public sealed class VideoOptions
{
    /// <summary>yt-dlp format selector, e.g. "bv*[height<=1080]+ba/b[height<=1080]".</summary>
    public string Format { get; set; } = "bv*+ba/b";
    /// <summary>mp4 / mkv / webm, or empty for "keep".</summary>
    public string Container { get; set; } = "mp4";
    public bool AudioOnly { get; set; }
    /// <summary>m4a / mp3 / opus when AudioOnly.</summary>
    public string AudioFormat { get; set; } = "m4a";
    public bool Playlist { get; set; }
    public bool Subtitles { get; set; }
    /// <summary>Empty = the app language and English.</summary>
    public string SubtitleLangs { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>
/// A download as shown in the list and persisted to disk. Properties are only changed on the owner
/// thread (the UI thread in the app); the engine works on its own copy of the state.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _fileName = "";
    private string _folder = "";
    private long _totalBytes = -1;
    private long _downloadedBytes;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _error;
    private int _connections = 8;
    private int _activeConnections;
    private double _speed;
    private TimeSpan? _eta;
    private string _url = "";
    private DateTime? _completedAt;
    private string? _statusDetail;
    private bool _supportsResume;
    private string? _sha256;
    private int _speedLimitKBps;
    private string? _queueId;
    private DateTime? _retryAt;

    public Guid Id { get; set; } = Guid.NewGuid();

    public DownloadKind Kind { get; set; } = DownloadKind.Http;

    public string Url { get => _url; set => Set(ref _url, value); }

    /// <summary>URL after redirects, used for segment requests.</summary>
    public string? FinalUrl { get; set; }

    public string? Referrer { get; set; }

    /// <summary>Page the download came from (for video jobs: the page given to yt-dlp).</summary>
    public string? PageUrl { get; set; }

    public string? UserAgent { get; set; }

    public string FileName { get => _fileName; set { if (Set(ref _fileName, value)) { Raise(nameof(FullPath)); Raise(nameof(Category)); } } }

    public string Folder { get => _folder; set { if (Set(ref _folder, value)) Raise(nameof(FullPath)); } }

    public long TotalBytes { get => _totalBytes; set { if (Set(ref _totalBytes, value)) Raise(nameof(Progress)); } }

    public long DownloadedBytes { get => _downloadedBytes; set { if (Set(ref _downloadedBytes, value)) Raise(nameof(Progress)); } }

    public DownloadStatus Status { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(IsActive)); Raise(nameof(Progress)); } } }

    public string? Error { get => _error; set => Set(ref _error, value); }

    /// <summary>Requested number of parallel connections (1–16).</summary>
    public int Connections { get => _connections; set => Set(ref _connections, Math.Clamp(value, 1, 16)); }

    public bool SupportsResume { get => _supportsResume; set => Set(ref _supportsResume, value); }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }

    public List<SegmentState> Segments { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? CompletedAt { get => _completedAt; set => Set(ref _completedAt, value); }

    /// <summary>Position in the queue; lower starts first.</summary>
    public long QueueOrder { get; set; }

    /// <summary>Queue this item waits in ("download later"), started by "Start queue" or the queue's schedule; null = none.</summary>
    public string? QueueId { get => _queueId; set => Set(ref _queueId, string.IsNullOrEmpty(value) ? null : value); }

    /// <summary>In any queue. Lists from before named queues only stored this flag: true there means the main queue.</summary>
    public bool InQueue
    {
        get => _queueId != null;
        set { if (value) QueueId ??= QueueInfo.MainId; else QueueId = null; }
    }

    /// <summary>The file name is still a guess and may be replaced by the server's name on first start.</summary>
    public bool AutoName { get; set; }

    /// <summary>Browser cookies for this download, DPAPI-encrypted (current user). Removed when the download finishes.</summary>
    public string? ProtectedCookies { get; set; }

    public VideoOptions? Video { get; set; }

    /// <summary>Expected SHA-256 given by the user; checked after completion when set.</summary>
    public string? ExpectedSha256 { get; set; }

    public string? Sha256 { get => _sha256; set => Set(ref _sha256, value); }

    /// <summary>Speed limit for this download only, KB/s (0 = none; the global limit still applies).</summary>
    public int SpeedLimitKBps { get => _speedLimitKBps; set => Set(ref _speedLimitKBps, Math.Max(0, value)); }

    /// <summary>When a failed download (connection lost, server busy) is tried again automatically.</summary>
    [JsonIgnore] public DateTime? RetryAt { get => _retryAt; set => Set(ref _retryAt, value); }
    [JsonIgnore] public int AutoRetries { get; set; }

    // ---- runtime only ----

    [JsonIgnore] public double Speed { get => _speed; set => Set(ref _speed, value); }
    [JsonIgnore] public TimeSpan? Eta { get => _eta; set => Set(ref _eta, value); }
    [JsonIgnore] public int ActiveConnections { get => _activeConnections; set => Set(ref _activeConnections, value); }
    [JsonIgnore] public string? StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }

    [JsonIgnore] public string FullPath => string.IsNullOrEmpty(Folder) || string.IsNullOrEmpty(FileName) ? "" : Path.Combine(Folder, FileName);
    [JsonIgnore] public string TempPath => FullPath + ".pdmdl";
    [JsonIgnore] public double Progress => TotalBytes > 0 ? Math.Min(100.0, DownloadedBytes * 100.0 / TotalBytes) : (Status == DownloadStatus.Completed ? 100 : 0);
    [JsonIgnore] public FileCategory Category => FileCategories.Of(FileName);
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Processing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
