using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PDM.Core.Security;

namespace PDM.Core.Video;

public enum ConvertTarget
{
    /// <summary>Sound only, MP3.</summary>
    Mp3,
    /// <summary>MP4 that plays everywhere: H.264 picture, AAC sound (streams that already fit are copied, not re-encoded).</summary>
    Mp4,
}

/// <summary>Converts a finished file with FFmpeg. Writes to a temporary name and renames at the end, so a cancelled
/// conversion leaves nothing half-made behind.</summary>
public sealed partial class MediaConverter(ToolManager tools)
{
    [GeneratedRegex(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"Stream #\d+:\d+[^:]*:\s*Video:\s*(\w+)(.*)")]
    private static partial Regex VideoPattern();

    [GeneratedRegex(@"Stream #\d+:\d+[^:]*:\s*Audio:\s*(\w+)")]
    private static partial Regex AudioPattern();

    /// <summary>0…1 while converting (from FFmpeg's thread).</summary>
    public Action<double>? Progress { get; set; }

    public sealed record MediaInfo(string? Video, string? Audio, TimeSpan? Duration);

    /// <summary>"Folder\clip.webm" → "Folder\clip.mp4" (or "clip (1).mp4" when taken, or when it is the input itself).</summary>
    public static string TargetPath(string input, ConvertTarget target)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input) + (target == ConvertTarget.Mp3 ? ".mp3" : ".mp4");
        return Path.Combine(dir, FileNames.MakeUnique(dir, name));
    }

    public static MediaInfo ParseInfo(string ffmpegOutput)
    {
        TimeSpan? duration = null;
        var d = DurationPattern().Match(ffmpegOutput);
        if (d.Success)
        {
            duration = TimeSpan.FromHours(int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture))
                + TimeSpan.FromMinutes(int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture))
                + TimeSpan.FromSeconds(double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        // A cover picture in a music file is not a video.
        var video = VideoPattern().Matches(ffmpegOutput).FirstOrDefault(m => !m.Groups[2].Value.Contains("attached pic", StringComparison.OrdinalIgnoreCase));
        var audio = AudioPattern().Match(ffmpegOutput);
        return new MediaInfo(video?.Groups[1].Value.ToLowerInvariant(), audio.Success ? audio.Groups[1].Value.ToLowerInvariant() : null, duration);
    }

    /// <summary>FFmpeg arguments after the input (codec choice), or null when the target cannot be made from this file.</summary>
    public static List<string>? CodecArguments(MediaInfo info, ConvertTarget target)
    {
        var a = new List<string>();
        if (target == ConvertTarget.Mp3)
        {
            if (info.Audio == null) return null;
            a.AddRange(["-map", "0:a:0", "-vn"]);
            if (info.Audio == "mp3") a.AddRange(["-c:a", "copy"]);
            else a.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]);
            return a;
        }
        if (info.Video == null) return null;
        a.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"]);
        if (info.Video == "h264") a.AddRange(["-c:v", "copy"]);
        else a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p"]);
        if (info.Audio is "aac" or "mp3" or null) a.AddRange(["-c:a", "copy"]);
        else a.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        a.AddRange(["-movflags", "+faststart"]);
        return a;
    }

    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct)
    {
        var (_, _, err) = await RunAsync(["-hide_banner", "-nostdin", "-i", input], null, ct).ConfigureAwait(false);
        return ParseInfo(err); // FFmpeg prints the streams and ends with "no output file"; that is expected
    }

    /// <summary>Converts <paramref name="input"/> and returns the new file's path.</summary>
    public async Task<string> ConvertAsync(string input, ConvertTarget target, CancellationToken ct)
    {
        if (!tools.HasFfmpeg) throw new InvalidOperationException("ffmpeg-missing");
        if (!File.Exists(input)) throw new FileNotFoundException("input", input);
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var codec = CodecArguments(info, target) ?? throw new NotSupportedException(target == ConvertTarget.Mp3 ? "no-audio" : "no-video");
        var output = TargetPath(input, target);
        if (!DiskSpace.HasRoomFor(output, new FileInfo(input).Length)) throw DiskSpace.NotEnoughSpace();
        var tmp = output + ".pdmconv" + Path.GetExtension(output);
        TryDelete(tmp);

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", input };
        args.AddRange(codec);
        args.AddRange(["-progress", "pipe:1", "-nostats", tmp]);
        var duration = info.Duration?.TotalSeconds ?? 0;
        void OnLine(string line)
        {
            // out_time_us (older builds: out_time_ms, also microseconds)
            if (duration <= 0 || !(line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))) return;
            if (long.TryParse(line.AsSpan(line.IndexOf('=') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                Progress?.Invoke(Math.Clamp(us / 1_000_000.0 / duration, 0, 1));
        }
        try
        {
            var (code, _, err) = await RunAsync(args, OnLine, ct).ConfigureAwait(false);
            if (code != 0 || !File.Exists(tmp))
            {
                var line = err.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                throw new IOException("ffmpeg exit " + code + (line.Length > 0 ? ": " + (line.Length > 200 ? line[..200] : line) : ""));
            }
            File.Move(tmp, output);
            Progress?.Invoke(1);
            return output;
        }
        finally { TryDelete(tmp); }
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(List<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tools.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; onLine?.Invoke(e.Data); if (onLine == null) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { if (err.Length < 64 * 1024) err.AppendLine(e.Data); } };
        p.Start();
        using var job = ToolJob.Contain(p, singleProcess: true); // FFmpeg reads an untrusted file: no other programs
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
            throw;
        }
        p.WaitForExit(); // flush the output events
        lock (err) return (p.ExitCode, output.ToString(), err.ToString());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
