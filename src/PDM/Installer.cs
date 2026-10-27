using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using PDM.Core;
using PDM.Core.Ipc;

namespace PDM;

/// <summary>Per-user install/uninstall: %LOCALAPPDATA%\Programs\PDM, shortcuts, HKCU keys. No admin rights.</summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PDM";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstalledExe => Path.Combine(AppPaths.InstallDir, "PDM.exe");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PDM.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PDM.lnk");

    public static void SetRunAtStartup(bool on, string? exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on && exePath != null) key.SetValue("PDM", "\"" + exePath + "\" --tray");
        else key.DeleteValue("PDM", throwOnMissingValue: false);
    }

    /// <summary>Asks a running PDM to exit and waits until it has.</summary>
    public static void StopRunningApp()
    {
        if (!IsAppRunning()) return;
        Program.SendToRunningInstance(["--exit"]);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (IsAppRunning() && DateTime.UtcNow < until) Thread.Sleep(250);
    }

    private static bool IsAppRunning()
    {
        if (!Mutex.TryOpenExisting(AppPaths.MutexName, out var m)) return false;
        m.Dispose();
        return true;
    }

    public static void Install(bool desktopShortcut, bool startWithWindows, bool browserIntegration)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        StopRunningApp();
        Directory.CreateDirectory(AppPaths.InstallDir);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            for (var attempt = 0; ; attempt++)
            {
                try { File.Copy(source, InstalledExe, overwrite: true); break; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
            }
        }

        Native.CreateShortcut(StartMenuLink, InstalledExe, "", "PDM Download Manager");
        if (desktopShortcut) Native.CreateShortcut(DesktopLink, InstalledExe, "", "PDM Download Manager");
        else TryDelete(DesktopLink);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "PDM Download Manager");
            key.SetValue("DisplayVersion", AppPaths.Version);
            key.SetValue("Publisher", "PDM");
            key.SetValue("DisplayIcon", InstalledExe + ",0");
            key.SetValue("InstallLocation", AppPaths.InstallDir);
            key.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }

        var settings = Store.LoadSettings();
        settings.StartWithWindows = startWithWindows;
        Store.SaveSettings(settings);
        SetRunAtStartup(startWithWindows, InstalledExe);

        if (browserIntegration)
        {
            using var host = Assembly.GetExecutingAssembly().GetManifestResourceStream("PDM.NativeHost.exe");
            if (host != null)
            {
                NativeHostRegistration.EnsureHostExe(host);
                NativeHostRegistration.Register([ExtensionInfo.Origin, .. settings.ExtraAllowedOrigins], InstalledExe);
            }
            PdmApp.ExtractExtension();
        }
        Log.Info("Installed to " + AppPaths.InstallDir);
    }

    public static void StartUninstallFromApp(PdmApp app)
    {
        var exe = File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath;
        if (exe == null) return;
        try { Process.Start(new ProcessStartInfo(exe, "--uninstall") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error("Starting uninstaller failed", ex); }
    }

    /// <summary>Entry point for "PDM.exe --uninstall".</summary>
    public static int RunUninstall()
    {
        AppPaths.EnsureDirectories();
        Log.Init(AppPaths.LogDir);
        Loc.Init(Store.LoadSettings().Language);
        if (MessageBox.Show(Loc.T("UninstallConfirm"), Loc.T("SetupTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 1;
        var deleteData = MessageBox.Show(Loc.T("UninstallData"), Loc.T("SetupTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        StopRunningApp();
        NativeHostRegistration.Unregister();
        try { SetRunAtStartup(false, null); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        TryDeleteDir(PdmApp.ExtensionDir);
        TryDeleteDir(AppPaths.HostDir);
        if (deleteData) TryDeleteDir(AppPaths.DataRoot);

        // This exe may be the installed one (in use), so the folder is removed a few seconds after we exit.
        var leftovers = new System.Collections.Generic.List<string>();
        if (Directory.Exists(AppPaths.InstallDir)) leftovers.Add(AppPaths.InstallDir);
        if (deleteData && Directory.Exists(AppPaths.DataRoot)) leftovers.Add(AppPaths.DataRoot);
        if (leftovers.Count > 0)
        {
            var cmd = "ping 127.0.0.1 -n 4 > nul";
            foreach (var d in leftovers) cmd += " & rmdir /s /q \"" + d + "\"";
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + cmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            catch (Exception ex) { Log.Error("Delayed delete failed", ex); }
        }
        MessageBox.Show(Loc.T("UninstallDone"), Loc.T("SetupTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
