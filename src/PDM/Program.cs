using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PDM.Core;
using PDM.Core.Ipc;

// Native calls go only to Windows' own DLLs in System32: a planted dwmapi.dll/powrprof.dll next to PDM-Setup.exe
// (often run from the Downloads folder) is never loaded.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace PDM;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        PDM.Core.Hardening.Apply();
        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "PDM");

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            return Installer.RunUninstall();

        if (exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase) || args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            return SetupWindow.Run();

        // Started by the updater: wait until the old version has exited and released the single-instance mutex.
        var wait = Array.FindIndex(args, a => a.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase));
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out var pid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(pid); old.WaitForExit(30_000); } catch { }
        }

        using var mutex = new Mutex(true, AppPaths.MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: hand over the arguments (or just "show") and quit.
            Native.AllowSetForegroundWindow(-1);
            SendToRunningInstance(args.Length > 0 ? args : ["--show"]);
            return 0;
        }
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) return 0;

        try
        {
            var app = new PdmApp(args);
            return app.Run();
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>Sends an "args" message to the running PDM. Returns false when no instance answered.</summary>
    public static bool SendToRunningInstance(string[] args, int timeoutMs = 3000)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new ArgsPayload { Args = args }, ProgramJson.Default.ArgsPayload);
            var reply = PipeClient.SendAsync(AppPaths.PipeName, payload, TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
            return reply != null;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class ArgsPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; set; } = "args";
    [System.Text.Json.Serialization.JsonPropertyName("args")] public string[] Args { get; set; } = [];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(ArgsPayload))]
internal partial class ProgramJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
