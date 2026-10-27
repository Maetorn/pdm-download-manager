using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PDM.Core;

/// <summary>
/// Windows job object around a helper tool that reads untrusted files (FFmpeg, tar, yt-dlp):
/// it cannot touch the clipboard, the desktop or system settings; with <c>singleProcess</c> it cannot start any
/// other program (so a malicious media file that took over FFmpeg could not launch anything); and it is ended
/// together with PDM (no left-over processes). Costs nothing while the tool runs.
/// </summary>
public sealed class ToolJob : IDisposable
{
    private readonly SafeFileHandle _job;

    private ToolJob(SafeFileHandle job) => _job = job;

    /// <summary>Puts a just-started process in a new job. Returns null where jobs are not available (not Windows).</summary>
    public static ToolJob? Contain(Process process, bool singleProcess)
    {
        if (!OperatingSystem.IsWindows()) return null;
        SafeFileHandle? job = null;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) return null;
            var limits = new ExtendedLimitInformation
            {
                Basic = new BasicLimitInformation
                {
                    LimitFlags = KillOnJobClose | DieOnUnhandledException | (singleProcess ? ActiveProcess : 0),
                    ActiveProcessLimit = singleProcess ? 1u : 0u,
                },
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new InvalidOperationException("limits");
            var ui = UiHandles | UiReadClipboard | UiWriteClipboard | UiSystemParameters | UiDisplaySettings | UiGlobalAtoms | UiDesktop | UiExitWindows;
            SetInformationJobObject(job, JobObjectBasicUIRestrictions, ref ui, sizeof(uint));
            if (!AssignProcessToJobObject(job, process.Handle)) throw new InvalidOperationException("assign");
            var result = new ToolJob(job);
            job = null;
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("Tool job not applied: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
        finally
        {
            job?.Dispose();
        }
    }

    /// <summary>For tests: the process is in this job and the child limit reads back as set.</summary>
    public bool Holds(Process process, out uint activeProcessLimit)
    {
        activeProcessLimit = 0;
        if (!IsProcessInJob(process.Handle, _job, out var inJob) || !inJob) return false;
        var limits = default(ExtendedLimitInformation);
        if (!QueryInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>(), IntPtr.Zero)) return false;
        activeProcessLimit = (limits.Basic.LimitFlags & ActiveProcess) != 0 ? limits.Basic.ActiveProcessLimit : 0;
        return true;
    }

    /// <summary>Closing the job ends whatever still runs in it.</summary>
    public void Dispose() => _job.Dispose();

    private const int JobObjectBasicUIRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint ActiveProcess = 0x8, DieOnUnhandledException = 0x400, KillOnJobClose = 0x2000;
    private const uint UiHandles = 0x1, UiReadClipboard = 0x2, UiWriteClipboard = 0x4, UiSystemParameters = 0x8,
        UiDisplaySettings = 0x10, UiGlobalAtoms = 0x20, UiDesktop = 0x40, UiExitWindows = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref uint info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
