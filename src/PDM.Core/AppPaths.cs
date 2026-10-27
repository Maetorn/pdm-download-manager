using System.Security.Principal;

// Native calls go only to Windows' own DLLs in System32 (never a same-named DLL next to the program).
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace PDM.Core;

public static class AppPaths
{
    public const string AppName = "PDM";
    /// <summary>Build version (build.ps1 -Version, e.g. "1.1.42"); "1.0.0" for local builds.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppPaths).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "1.0.0";
        var plus = info.IndexOf('+'); // "+<commit>" added by the SDK
        return plus > 0 ? info[..plus] : info;
    }
    public const string NativeHostName = "com.pdm.host";

    /// <summary>Override for tests.</summary>
    public static string? DataRootOverride { get; set; }

    /// <summary>%LOCALAPPDATA%\PDM — settings, download list, logs, tools.</summary>
    public static string DataRoot => DataRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string DataDir => Path.Combine(DataRoot, "data");
    public static string LogDir => Path.Combine(DataRoot, "logs");
    public static string ToolsDir => Path.Combine(DataRoot, "tools");
    public static string HostDir => Path.Combine(DataRoot, "host");

    /// <summary>Per-user install location: %LOCALAPPDATA%\Programs\PDM.</summary>
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static string? _sid;
    public static string UserSid => _sid ??= WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;

    /// <summary>Pipe name unique per Windows user. The pipe's ACL and owner check are what keep it private.</summary>
    public static string PipeName => "PDM.ipc." + UserSid;

    public static string MutexName => @"Local\PDM.single." + UserSid;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}
