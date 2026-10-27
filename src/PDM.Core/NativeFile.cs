using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PDM.Core;

internal static class NativeFile
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    /// <summary>
    /// Marks the file sparse so writing near the end of a large preallocated file does not make NTFS
    /// zero-fill everything before it (which would stall the first far-away segment for a long time).
    /// </summary>
    public static bool TrySetSparse(SafeFileHandle handle, bool sparse = true)
    {
        try
        {
            var buf = new byte[] { sparse ? (byte)1 : (byte)0 };
            return DeviceIoControl(handle, FSCTL_SET_SPARSE, buf, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    public static void Flush(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) return;
        // The engine is Windows-only; the managed flush keeps it testable elsewhere.
        if (OperatingSystem.IsWindows()) FlushFileBuffers(handle);
        else RandomAccess.FlushToDisk(handle);
    }

    public static bool IsDiskFull(Exception ex) =>
        ex is IOException io && ((io.HResult & 0xFFFF) is 0x70 or 0x27); // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);
}
