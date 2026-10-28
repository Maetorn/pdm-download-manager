using PDM.Core.Models;

namespace PDM.Core.Engine;

public sealed class AddRequest
{
    public string Url { get; set; } = "";
    /// <summary>Desired file name; null = decide from the server response.</summary>
    public string? FileName { get; set; }
    public string? Folder { get; set; }
    public string? Referrer { get; set; }
    public string? PageUrl { get; set; }
    public string? UserAgent { get; set; }
    public List<CookieData>? Cookies { get; set; }
    public int? Connections { get; set; }
    /// <summary>true = start immediately; false = add paused to the queue ("download later").</summary>
    public bool StartNow { get; set; } = true;
    /// <summary>Queue for "download later"; null = the main queue.</summary>
    public string? QueueId { get; set; }
    public long SizeHint { get; set; } = -1;
    public string? ContentType { get; set; }
    public string? ExpectedSha256 { get; set; }
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    public VideoOptions? Video { get; set; }

    public AddRequest Copy() => (AddRequest)MemberwiseClone();
}
