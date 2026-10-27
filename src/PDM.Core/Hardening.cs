using System.Runtime.InteropServices;

namespace PDM.Core;

/// <summary>
/// Process-wide protections set first thing at start: DLLs are no longer looked for in the current folder, never
/// loaded from network shares or from low-integrity (sandboxed-download) files, and Windows' own copies in System32
/// come first. One-time settings, no cost afterwards.
/// </summary>
public static class Hardening
{
    private const int ProcessImageLoadPolicy = 10;
    public const uint NoRemoteImages = 1, NoLowMandatoryLabelImages = 2, PreferSystem32Images = 4;

    public static void Apply()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetDllDirectoryW(""); } catch { }
        try
        {
            var flags = NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images;
            if (!SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref flags, sizeof(uint)))
                Log.Warn("Image load policy not applied: " + Marshal.GetLastPInvokeError());
        }
        catch (Exception ex) { Log.Warn("Image load policy: " + ex.GetType().Name); }
    }

    /// <summary>The image-load flags now in force for this process (for tests and diagnostics).</summary>
    public static uint ImageLoadFlags()
    {
        uint flags = 0;
        try { GetProcessMitigationPolicy(GetCurrentProcess(), ProcessImageLoadPolicy, ref flags, sizeof(uint)); } catch { }
        return flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string pathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, ref uint buffer, nint length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
