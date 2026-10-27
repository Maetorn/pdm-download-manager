using System.Text;

namespace PDM.Core;

/// <summary>
/// Minimal file logger. Callers must never pass cookies, headers or full URLs (use FileNames.Redact);
/// as a second line of defence, lines are scrubbed for obvious secrets before they are written.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static void Init(string directory)
    {
        _dir = directory;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var f in Directory.GetFiles(directory, "pdm-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {Scrub(ex.Message)}");

    private static void Write(string level, string message)
    {
        if (_dir == null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Scrub(message)}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"pdm-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8); }
            catch { }
        }
    }

    private static string Scrub(string s)
    {
        foreach (var key in new[] { "cookie", "authorization", "token=", "sig=", "signature=", "key=", "password" })
        {
            var i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s[..i] + key + "[redacted]";
        }
        return s.Replace('\r', ' ').Replace('\n', ' ');
    }
}
