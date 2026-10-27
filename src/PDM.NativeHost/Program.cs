using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PDM.NativeHost;

/// <summary>
/// Native messaging host started by the browser. It forwards each message from the extension to the
/// running PDM app over the per-user named pipe (tagged with the calling extension's origin) and writes
/// the answer back. It never touches the network or the disk except for starting PDM.exe.
/// stdout is reserved for the protocol, so nothing else is printed.
/// </summary>
internal static partial class Program
{
    private const int MaxMessage = 8 * 1024 * 1024;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [GeneratedRegex("^chrome-extension://[a-p]{32}/$")]
    private static partial Regex OriginPattern();

    /// <summary>Our Firefox add-on (Firefox passes the manifest path and the calling add-on's ID).</summary>
    private const string FirefoxId = "pdm-download-manager@maetorn";

    private static async Task<int> Main(string[] args)
    {
        // Chrome/Opera/Edge pass the caller's origin as the first argument (and --parent-window=N on Windows);
        // Firefox passes the path of our host manifest and the calling add-on's ID. Only our own add-on is accepted.
        var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal))
            ?? (args.Contains(FirefoxId, StringComparer.Ordinal) ? FirefoxId : null);
        if (origin == null || (origin != FirefoxId && !OriginPattern().IsMatch(origin))) return 2;

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var pipeName = "PDM.ipc." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

        while (true)
        {
            byte[]? message;
            try { message = await ReadFrameAsync(stdin).ConfigureAwait(false); }
            catch { return 1; }
            if (message == null) return 0; // browser closed the port

            byte[] reply;
            try
            {
                reply = await ForwardAsync(pipeName, origin, message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                reply = Error(ex is TimeoutException ? "not-running" : "bridge");
            }
            try { await WriteFrameAsync(stdout, reply).ConfigureAwait(false); }
            catch { return 1; }
        }
    }

    private static async Task<byte[]> ForwardAsync(string pipeName, string origin, byte[] message)
    {
        string type;
        try
        {
            using var doc = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Error("bad-json");
            type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return Error("bad-json");
        }

        // The app may bring its window to the front (the browser, our parent, has the foreground right): only PDM,
        // identified by the process at the other end of the pipe, gets that right.
        var wantsForeground = type is "download" or "video" or "show";

        // The message is already validated JSON; wrap it with the origin the browser told us.
        var wrapped = new byte[message.Length + origin.Length + 32];
        var prefix = Encoding.UTF8.GetBytes("{\"origin\":\"" + origin + "\",\"msg\":");
        var ms = new MemoryStream(wrapped.Length);
        ms.Write(prefix);
        ms.Write(message);
        ms.WriteByte((byte)'}');
        var payload = ms.ToArray();

        var reply = await TrySendAsync(pipeName, payload, TimeSpan.FromMilliseconds(800), wantsForeground).ConfigureAwait(false);
        if (reply != null) return reply;

        // App not running: start it for actions that need it, then retry for a while.
        if (type is not ("download" or "video" or "show")) return Error("not-running");
        if (!LaunchApp()) return Error("not-installed");
        var until = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(300).ConfigureAwait(false);
            reply = await TrySendAsync(pipeName, payload, TimeSpan.FromMilliseconds(500), wantsForeground).ConfigureAwait(false);
            if (reply != null) return reply;
        }
        return Error("not-running");
    }

    private static async Task<byte[]?> TrySendAsync(string pipeName, byte[] payload, TimeSpan connectTimeout, bool allowForeground)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)connectTimeout.TotalMilliseconds).ConfigureAwait(false);
            if (allowForeground && GetNamedPipeServerProcessId(client.SafePipeHandle, out var pdm)) AllowSetForegroundWindow((int)pdm);
            await WriteFrameAsync(client, payload).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await ReadFrameAsync(client, cts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; } // pipe owned by someone else: never talk to it
    }

    private static bool LaunchApp()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var pathFile = Path.Combine(dir, "app-path.txt");
            if (!File.Exists(pathFile)) return false;
            var exe = File.ReadAllText(pathFile).Trim();
            if (!Path.IsPathFullyQualified(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--tray");
            // Do not hand our stdio (the browser's pipes) to the app.
            psi.RedirectStandardInput = false;
            psi.RedirectStandardOutput = false;
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Error(string code) => Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"" + code + "\"}");

    private static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(s, header, ct).ConfigureAwait(false)) return null;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > MaxMessage) throw new InvalidDataException();
        var body = new byte[len];
        if (!await ReadExactlyAsync(s, body, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return body;
    }

    private static async Task<bool> ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }

    private static async Task WriteFrameAsync(Stream s, byte[] body)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await s.WriteAsync(header).ConfigureAwait(false);
        await s.WriteAsync(body).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }
}
