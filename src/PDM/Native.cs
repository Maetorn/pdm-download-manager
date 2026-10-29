using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDM;

/// <summary>Win32 calls used by the UI. Everything here is per-user and needs no admin rights.</summary>
internal static class Native
{
    // ---- dark title bar ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void SetDarkTitleBar(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }
        catch { }
    }

    // ---- foreground ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    public static void BringToFront(Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        // Topmost toggle helps when Windows refuses the foreground change.
        w.Topmost = true;
        w.Topmost = false;
        try { SetForegroundWindow(new WindowInteropHelper(w).Handle); } catch { }
        w.Focus();
    }

    // ---- power ----

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    // ---- priority ----

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    private const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000, PROCESS_MODE_BACKGROUND_END = 0x00200000;

    /// <summary>
    /// Background mode for this process: the system lowers its CPU, disk (I/O) and memory-page priority so that what the
    /// user is doing in the foreground is served first. Ended again when nothing downloads.
    /// </summary>
    public static bool SetBackgroundMode(bool on)
    {
        try { return SetPriorityClass(GetCurrentProcess(), on ? PROCESS_MODE_BACKGROUND_BEGIN : PROCESS_MODE_BACKGROUND_END); }
        catch { return false; }
    }

    // ---- memory ----

    /// <summary>(-1, -1) moves the process's unused pages out of RAM; they come back on demand.</summary>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    // ---- clipboard listener ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public const int WM_CLIPBOARDUPDATE = 0x031D;

    // ---- power ----

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    // ---- shell icons ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Small shell icon for a file type, by extension only (the file need not exist; nothing is executed).</summary>
    public static ImageSource? IconForExtension(string? fileName, bool large = false)
    {
        var ext = System.IO.Path.GetExtension(fileName ?? "");
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        // .exe/.lnk/.ico icons would come from the file itself; use a generic look for those.
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            ext = ext.ToLowerInvariant();
        return IconCache.GetOrAdd((large ? "L" : "S") + ext, key =>
        {
            try
            {
                var info = new SHFILEINFO();
                var flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (large ? 0 : SHGFI_SMALLICON);
                SHGetFileInfo("file" + key[1..], FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (info.hIcon == IntPtr.Zero) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DestroyIcon(info.hIcon); }
            }
            catch { return null; }
        });
    }

    // ---- shortcuts (.lnk) ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public static void CreateShortcut(string lnkPath, string target, string arguments, string description)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }
}
