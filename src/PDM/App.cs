using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PDM.Core;
using PDM.Core.Engine;
using PDM.Core.Ipc;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM;

internal sealed class PdmApp : Application
{
    private readonly string[] _startArgs;
    private PipeServer? _pipe;
    private HwndSource? _clipboardWindow;
    private DispatcherTimer? _scheduleTimer;
    private volatile HashSet<string> _allowedOrigins = new(StringComparer.Ordinal) { ExtensionInfo.Origin, ExtensionInfo.FirefoxId };
    private string? _lastClipboardUrl;
    private DateTime _ignoreClipboardUntil;
    private bool _exiting;
    private readonly Dictionary<Guid, ProgressWindow> _progressWindows = new();
    /// <summary>Downloads started one at a time by the user: they get the "Download complete" window.</summary>
    private readonly HashSet<Guid> _watched = new();

    public static new PdmApp Current => (PdmApp)Application.Current;

    public AppSettings Settings { get; private set; } = new();
    public DownloadManager Manager { get; private set; } = null!;
    public Tray Tray { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;

    public static string ExtensionDir => Path.Combine(AppPaths.DataRoot, "extension");
    public static string FirefoxDir => Path.Combine(AppPaths.DataRoot, "firefox");
    public static string FirefoxXpiPath => Path.Combine(FirefoxDir, "pdm-firefox.xpi");

    public PdmApp(string[] args)
    {
        _startArgs = args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureDirectories();
        Log.Init(AppPaths.LogDir);
        Log.Info($"PDM {AppPaths.Version} starting");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI error", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Fatal", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task", ex.Exception); ex.SetObserved(); };

        Settings = Store.LoadSettings();
#if UI_TOUR
        var tour = UiTour.Requested(_startArgs, out var tourDir, out var tourLang, out var tourTheme);
        if (tour) (Settings.Language, Settings.Theme) = (tourLang, tourTheme);
#endif
        NetConfig.Configure(Settings);
        Loc.Init(Settings.Language);
        try { Theme.Apply(this, Settings.Theme); }
        catch (Exception ex)
        {
            // A theme problem must never keep PDM from starting (it would hold the single-instance lock with no window).
            Log.Error("Theme failed, using the light theme", ex);
            try { Theme.Apply(this, "Light"); } catch { }
        }
        UpdateAllowedOrigins();

        Manager = new DownloadManager(Settings, new DispatcherSynchronizationContext(Dispatcher));
        Manager.Load();
        Manager.Completed += OnCompleted;
        Manager.Failed += OnFailed;
        Manager.QueueFinished += OnQueueFinished;

        _pipe = new PipeServer(AppPaths.PipeName, HandleBridgeAsync);
        _pipe.Start();

        Tray = new Tray(this);
        Main = new MainWindow(this);
        MainWindow = Main;

        Task.Run(SetUpBrowserIntegration);
        SetClipboardMonitor(Settings.ClipboardMonitor);

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();

        var tray = _startArgs.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!tray) Main.Show();
        HandleArgs(_startArgs, fromStart: true);

        Updates = new UpdateService(this);
        Updates.Start();
        if (_startArgs.Contains("--updated", StringComparer.OrdinalIgnoreCase))
            Tray.Balloon("PDM", Loc.F("UpdDone", AppPaths.Version), System.Windows.Forms.ToolTipIcon.Info);
#if UI_TOUR
        if (tour) _ = UiTour.RunAsync(this, tourDir);
#endif
    }

    // ---------------- browser bridge ----------------

    private void UpdateAllowedOrigins()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { ExtensionInfo.Origin, ExtensionInfo.FirefoxId };
        foreach (var o in Settings.ExtraAllowedOrigins) if (IsValidOrigin(o)) set.Add(o);
        _allowedOrigins = set;
    }

    public static bool IsValidOrigin(string? o) =>
        o != null && o.Length == 52 && o.StartsWith("chrome-extension://", StringComparison.Ordinal) && o.EndsWith('/') && o[19..51].All(c => c is >= 'a' and <= 'p');

    /// <summary>Runs on a pipe thread. Answers right away; UI work is posted to the dispatcher.</summary>
    private Task<byte[]> HandleBridgeAsync(byte[] request)
    {
        BridgeMessage msg;
        try { msg = BridgeProtocol.Parse(request, _allowedOrigins); }
        catch (BridgeException ex)
        {
            Log.Warn("Bridge message rejected: " + ex.Code);
            return Task.FromResult(BridgeProtocol.Error(ex.Code));
        }

        switch (msg)
        {
            case PingMessage:
                return Task.FromResult(BridgeProtocol.Ok());
            case ShowMessage:
                Dispatcher.BeginInvoke(ShowMain);
                return Task.FromResult(BridgeProtocol.Ok());
            case ArgsMessage a:
                Dispatcher.BeginInvoke(() => HandleArgs(a.Args, fromStart: false));
                return Task.FromResult(BridgeProtocol.Ok());
            // The extension cancels the browser's own download when PDM says ok, so ok is only sent once PDM really
            // has it (added to the list, or its window is open). Anything else answers an error and the browser keeps it.
            case DownloadMessage d:
                return OnUiAsync(() => HandleDownloads(d), BridgeProtocol.Ok(d.Items.Count));
            case VideoMessage v:
                return OnUiAsync(() =>
                {
                    new VideoWindow(this, v.PageUrl, v.Title, v.Media, v.UserAgent, v.Cookies, v.Referrer).ShowAndActivate();
                    return true;
                }, BridgeProtocol.Ok(1));
            case UpdateExtensionMessage { Firefox: true }:
                Dispatcher.BeginInvoke(() => InstallFirefoxExtension(quiet: true));
                return Task.FromResult(BridgeProtocol.Ok());
            default:
                return Task.FromResult(BridgeProtocol.Error("type"));
        }
    }

    /// <summary>Runs <paramref name="act"/> on the UI thread; <paramref name="ok"/> if it succeeded, an error otherwise.</summary>
    private async Task<byte[]> OnUiAsync(Func<bool> act, byte[] ok)
    {
        try
        {
            return await Dispatcher.InvokeAsync(act) ? ok : BridgeProtocol.Error("app");
        }
        catch (Exception ex)
        {
            Log.Error("Browser request failed", ex);
            return BridgeProtocol.Error("app");
        }
    }

    /// <summary>True when PDM took the download(s): added to the list or shown in its add window.</summary>
    private bool HandleDownloads(DownloadMessage m)
    {
        try
        {
            if (m.Items.Count == 1)
            {
                var req = m.Items[0];
                if (m.Interactive || Settings.ShowDialogForBrowserDownloads)
                {
                    new AddDownloadWindow(this, req).ShowAndActivate();
                }
                else
                {
                    req.Folder ??= Settings.DownloadFolder;
                    ShowProgress(Manager.Add(req));
                }
            }
            else
            {
                new AddDownloadWindow(this, m.Items).ShowAndActivate();
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not add browser download", ex);
            return false;
        }
    }

    public void HandleArgs(string[] args, bool fromStart)
    {
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            _ = ExitAsync(ask: false);
            return;
        }
        var urls = args.Where(a => FileNames.IsHttpUrl(a, out _)).ToList();
        if (urls.Count == 1) new AddDownloadWindow(this, new AddRequest { Url = urls[0] }).ShowAndActivate();
        else if (urls.Count > 1) new AddDownloadWindow(this, urls.Select(u => new AddRequest { Url = u }).ToList()).ShowAndActivate();
        else if (!fromStart && !args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowMain();
    }

    /// <summary>Extracts the embedded native host and extension and registers the host (HKCU). Runs on every start.</summary>
    private void SetUpBrowserIntegration()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var host = asm.GetManifestResourceStream("PDM.NativeHost.exe"))
            {
                if (host != null) NativeHostRegistration.EnsureHostExe(host);
                else Log.Warn("Native host is not embedded in this build");
            }
            if (File.Exists(NativeHostRegistration.HostExePath) && Environment.ProcessPath is { } exe)
                NativeHostRegistration.Register(_allowedOrigins, exe);
        }
        catch (Exception ex) { Log.Error("Native host registration failed", ex); }

        try { BridgeProtocol.ExtensionVersion = ExtractExtension(); }
        catch (Exception ex) { Log.Error("Extension extraction failed", ex); }
        try { ExtractFirefoxExtension(); }
        catch (Exception ex) { Log.Error("Firefox add-on extraction failed", ex); }
        Dispatcher.BeginInvoke(() => Main?.ResetBrowserStatus());
    }

    // ---------------- Firefox ----------------

    /// <summary>Puts the embedded Firefox add-on (.xpi) in %LOCALAPPDATA%\PDM\firefox when it changed.</summary>
    private static void ExtractFirefoxExtension()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("firefox.xpi");
        if (res == null) return;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var bytes = ms.ToArray();
        Directory.CreateDirectory(FirefoxDir);
        if (File.Exists(FirefoxXpiPath) && File.ReadAllBytes(FirefoxXpiPath).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(FirefoxXpiPath + ".new", bytes);
        File.Move(FirefoxXpiPath + ".new", FirefoxXpiPath, overwrite: true);
    }

    /// <summary>Signed by Mozilla: only then does release Firefox keep it installed.</summary>
    public static bool IsSignedXpi(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(e => e.FullName is "META-INF/mozilla.rsa" or "META-INF/cose.sig");
        }
        catch { return false; }
    }

    /// <summary>firefox.exe from "App Paths" (where Windows looks it up), else the usual install folders.</summary>
    public static string? FindFirefox()
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
                if (key?.GetValue("") is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Firefox", "firefox.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Opens the add-on in Firefox, which asks to add (or update) it. An unsigned build cannot stay installed in
    /// release Firefox: then about:debugging is opened for a temporary load and the file's path is copied.
    /// <paramref name="quiet"/>: asked by the add-on itself after a PDM update (no message when Firefox is missing).
    /// </summary>
    public void InstallFirefoxExtension(bool quiet = false)
    {
        var owner = PdmWindow.VisibleOwner();
        var firefox = FindFirefox();
        if (firefox == null || !File.Exists(FirefoxXpiPath))
        {
            if (!quiet) Ui.Info(owner, Loc.T(firefox == null ? "FirefoxMissing" : "FirefoxNoXpi"));
            return;
        }
        try
        {
            if (IsSignedXpi(FirefoxXpiPath))
            {
                Process.Start(new ProcessStartInfo(firefox) { ArgumentList = { FirefoxXpiPath }, UseShellExecute = false });
                return;
            }
            if (quiet) return; // an unsigned build can only be loaded by hand
            try { Clipboard.SetText(FirefoxXpiPath); } catch { }
            Process.Start(new ProcessStartInfo(firefox) { ArgumentList = { "about:debugging#/runtime/this-firefox" }, UseShellExecute = false });
            if (!quiet) Ui.Info(owner, Loc.F("FirefoxUnsigned", FirefoxXpiPath));
        }
        catch (Exception ex) { Log.Error("Opening Firefox failed", ex); }
    }

    public void ReRegister()
    {
        Task.Run(SetUpBrowserIntegration);
    }

    private static string? ManifestVersion(Stream manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Puts the embedded extension in %LOCALAPPDATA%\PDM\extension when its version differs. Returns the version now on
    /// disk (the extension reloads itself when it sees a newer one), or null when nothing could be written.
    /// </summary>
    internal static string? ExtractExtension()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("extension.zip");
        if (res == null) return null;
        using var zip = new ZipArchive(res, ZipArchiveMode.Read);
        var entry = zip.GetEntry("manifest.json");
        if (entry == null) return null;
        string? newVersion;
        using (var s = entry.Open()) newVersion = ManifestVersion(s);
        var dir = ExtensionDir;
        var current = Path.Combine(dir, "manifest.json");
        if (File.Exists(current))
        {
            using var s = File.OpenRead(current);
            if (ManifestVersion(s) == newVersion) return newVersion;
        }
        // Extract next to it first (ExtractToDirectory refuses entries that would land outside the folder),
        // then swap; if Opera holds a file open, copy over the old files instead.
        var staging = dir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        zip.ExtractToDirectory(staging);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Move(staging, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dir, Path.GetRelativePath(staging, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
        Log.Info($"Extension {newVersion} extracted");
        return newVersion;
    }

    // ---------------- events ----------------

    /// <summary>Called after the user started a single download: IDM-style progress window (if enabled).</summary>
    public void ShowProgress(DownloadItem item)
    {
        _watched.Add(item.Id);
        if (Settings.ShowProgressWindow && item.Status != DownloadStatus.Completed) OpenProgress(item);
    }

    public void OpenProgress(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var existing))
        {
            Native.BringToFront(existing);
            return;
        }
        var w = new ProgressWindow(this, item);
        _progressWindows[item.Id] = w;
        w.Closed += (_, _) => _progressWindows.Remove(item.Id);
        w.ShowAndActivate();
    }

    private void OnCompleted(DownloadItem item)
    {
        if (Settings.AutoExtract && Archives.IsArchive(item.FileName)) _ = ExtractAsync(item, automatic: true);
        var watched = _watched.Remove(item.Id) | _progressWindows.ContainsKey(item.Id);
        if (_progressWindows.TryGetValue(item.Id, out var pw)) pw.Close();
        if (watched && Settings.ShowCompleteDialog)
        {
            new CompleteWindow(this, item).ShowAndActivate();
            return;
        }
        if (Settings.NotifyOnComplete)
            Tray.Balloon(Loc.T("NotifyDone"), item.FileName, System.Windows.Forms.ToolTipIcon.Info, () => OpenFolder(item));
    }

    private void OnFailed(DownloadItem item)
    {
        if (Settings.NotifyOnComplete)
            Tray.Balloon(Loc.T("NotifyFailed"), item.FileName + "\n" + Loc.ErrorText(item.Error), System.Windows.Forms.ToolTipIcon.Warning, ShowMain);
    }

    private void OnQueueFinished(string queueId)
    {
        var action = Settings.FindQueue(queueId)?.PostAction ?? PostQueueAction.None;
        if (action == PostQueueAction.None) return;
        // Exit / sleep / shut down only when nothing else is still downloading.
        if (Manager.ActiveCount > 0) return;
        if (action == PostQueueAction.ExitApp) { _ = ExitAsync(ask: false); return; }
        new CountdownWindow(this, action).ShowAndActivate();
    }

    public async void RunPostAction(PostQueueAction action)
    {
        Log.Info("Post-queue action: " + action);
        if (action == PostQueueAction.Sleep)
        {
            Native.SetSuspendState(false, false, false);
        }
        else if (action == PostQueueAction.Shutdown)
        {
            await Manager.ShutdownAsync();
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { Log.Error("Shutdown failed", ex); }
        }
    }

    private void CheckSchedule()
    {
        var now = DateTime.Now;
        var changed = false;
        foreach (var q in Settings.Queues.ToList())
        {
            if (!q.ScheduleEnabled) continue;
            if (q.StartAt is { } start && start <= now)
            {
                if (now - start < TimeSpan.FromMinutes(5) && q.RunsOn(start.DayOfWeek)) { Log.Info("Scheduler: starting queue " + q.Id); Manager.StartQueue(q.Id); }
                while (start <= now) start = start.AddDays(1); // repeats daily (on the chosen days)
                q.StartAt = start;
                changed = true;
            }
            if (q.StopAt is { } stop && stop <= now)
            {
                if (now - stop < TimeSpan.FromMinutes(5) && q.RunsOn(stop.DayOfWeek)) { Log.Info("Scheduler: stopping queue " + q.Id); Manager.StopQueue(q.Id); }
                while (stop <= now) stop = stop.AddDays(1);
                q.StopAt = stop;
                changed = true;
            }
        }
        if (changed) SaveSettings();
    }

    // ---------------- settings ----------------

    public void SaveSettings()
    {
        Settings.Normalize();
        NetConfig.Configure(Settings);
        try { Store.SaveSettings(Settings); } catch (Exception ex) { Log.Error("Saving settings failed", ex); }
        Manager.ApplySettings(Settings);
        UpdateAllowedOrigins();
        SetClipboardMonitor(Settings.ClipboardMonitor);
        try { Installer.SetRunAtStartup(Settings.StartWithWindows, Environment.ProcessPath); } catch (Exception ex) { Log.Error("Run key", ex); }
    }

    // ---------------- power / speed ----------------

    private bool _keepAwake;

    // ---------------- memory ----------------

    private DateTime _idleSince = DateTime.MaxValue;
    private bool _trimmed;

    /// <summary>
    /// Called every second. After 20 s with no window open and nothing downloading, gives memory back once:
    /// a full garbage collection and a working-set trim (unused pages leave RAM and return only if needed).
    /// Any download or opened window starts the count again.
    /// </summary>
    public void NoteActivity()
    {
        var idle = Manager.ActiveCount == 0 && !Windows.OfType<Window>().Any(w => w.IsVisible);
        if (!idle)
        {
            _idleSince = DateTime.MaxValue;
            _trimmed = false;
            return;
        }
        if (_idleSince == DateTime.MaxValue) _idleSince = DateTime.UtcNow;
        if (_trimmed || DateTime.UtcNow - _idleSince < TimeSpan.FromSeconds(20)) return;
        _trimmed = true;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }

    /// <summary>Keeps Windows from sleeping while something downloads (screen may still turn off).</summary>
    public void UpdatePowerState()
    {
        var want = Settings.PreventSleep && Manager.ActiveCount > 0;
        if (want == _keepAwake) return;
        _keepAwake = want;
        Native.SetThreadExecutionState(want ? Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED : Native.ES_CONTINUOUS);
    }

    /// <summary>Quick switch for the global speed limit (remembers the last value).</summary>
    public void ToggleSpeedLimit()
    {
        if (Settings.SpeedLimitKBps > 0)
        {
            Settings.LastSpeedLimitKBps = Settings.SpeedLimitKBps;
            Settings.SpeedLimitKBps = 0;
        }
        else
        {
            Settings.SpeedLimitKBps = Settings.LastSpeedLimitKBps > 0 ? Settings.LastSpeedLimitKBps : 1024;
        }
        SaveSettings();
        Tray.Balloon("PDM", Settings.SpeedLimitKBps > 0 ? Loc.F("StatusBarLimit", Ui.Size(Settings.SpeedLimitKBps * 1024L)) : Loc.T("NoLimit"), System.Windows.Forms.ToolTipIcon.None);
    }

    // ---------------- clipboard ----------------

    public void IgnoreClipboardBriefly() => _ignoreClipboardUntil = DateTime.UtcNow.AddSeconds(2);

    private void SetClipboardMonitor(bool on)
    {
        if (on && _clipboardWindow == null)
        {
            // Message-only window (HWND_MESSAGE) that receives WM_CLIPBOARDUPDATE.
            var p = new HwndSourceParameters("PDM.Clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
            _clipboardWindow = new HwndSource(p);
            _clipboardWindow.AddHook(ClipboardHook);
            Native.AddClipboardFormatListener(_clipboardWindow.Handle);
        }
        else if (!on && _clipboardWindow != null)
        {
            Native.RemoveClipboardFormatListener(_clipboardWindow.Handle);
            _clipboardWindow.Dispose();
            _clipboardWindow = null;
        }
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, OnClipboardChanged);
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (DateTime.UtcNow < _ignoreClipboardUntil) return;
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText().Trim();
        }
        catch { return; } // clipboard busy
        if (text.Length > 4096 || text.Contains('\n') || !FileNames.IsHttpUrl(text, out var uri)) return;
        if (text == _lastClipboardUrl) return;
        if (!LooksLikeDownload(uri!)) return;
        _lastClipboardUrl = text;
        Tray.Balloon("PDM", Loc.T("ClipboardFound") + "\n" + FileNames.Redact(text), System.Windows.Forms.ToolTipIcon.Info,
            () => new AddDownloadWindow(this, new AddRequest { Url = text }).ShowAndActivate());
    }

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase) { "", ".html", ".htm", ".php", ".asp", ".aspx", ".jsp", ".cgi" };

    private static bool LooksLikeDownload(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (PageExtensions.Contains(ext)) return false;
        return FileCategories.Of("x" + ext) != FileCategory.Other || ext.Length is > 1 and <= 6;
    }

    // ---------------- shell ----------------

    public void ShowMain() => Native.BringToFront(Main);

    public void Open(DownloadItem item)
    {
        var path = item.FullPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { OpenFolder(item); return; }
        if (Settings.WarnBeforeOpeningExecutables && FileCategories.IsExecutable(path))
        {
            if (!Ui.Confirm(PdmWindow.VisibleOwner(), Loc.F("OpenExeText", item.FileName, FileNames.Redact(item.FinalUrl ?? item.Url)), Loc.T("OpenExeTitle"), MessageBoxImage.Warning))
                return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open failed", ex); }
    }

    public void OpenFolder(DownloadItem item)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
                Process.Start(explorer, "/select,\"" + item.FullPath + "\"");
            else if (Directory.Exists(item.Folder))
                Process.Start(explorer, "\"" + item.Folder + "\"");
        }
        catch (Exception ex) { Log.Error("Open folder failed", ex); }
    }

    // ---------------- archives ----------------

    private readonly HashSet<string> _extracting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extracts a finished archive next to itself; the balloon opens the new folder.</summary>
    public async Task ExtractAsync(DownloadItem item, bool automatic)
    {
        var path = item.FullPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !_extracting.Add(path)) return;
        try
        {
            var folder = await Archives.ExtractAsync(path);
            Log.Info("Extracted " + item.FileName);
            if (automatic && Settings.DeleteArchiveAfterExtract && RecycleBin.Send(path)) Log.Info("Archive moved to the Recycle Bin");
            Tray.Balloon(Loc.T("Extracted"), Path.GetFileName(folder), System.Windows.Forms.ToolTipIcon.Info, () => OpenPath(folder));
        }
        catch (Exception ex)
        {
            Log.Warn($"Extract failed: {ex.GetType().Name}: {ex.Message}");
            var reason = DiskSpace.IsDiskFull(ex) ? Loc.ErrorText("diskfull")
                : ex is NotSupportedException ? Loc.T("ExtractNoTar")
                : Loc.T("ExtractBad");
            Tray.Balloon(Loc.T("ExtractFailed"), item.FileName + "\n" + reason, System.Windows.Forms.ToolTipIcon.Warning);
        }
        finally { _extracting.Remove(path); }
    }

    /// <summary>Opens a file from the history (same executable warning as the list).</summary>
    public void OpenFile(string path, string? sourceUrl)
    {
        if (!File.Exists(path)) { SelectInExplorer(path); return; }
        if (Settings.WarnBeforeOpeningExecutables && FileCategories.IsExecutable(path))
        {
            if (!Ui.Confirm(PdmWindow.VisibleOwner(), Loc.F("OpenExeText", Path.GetFileName(path), FileNames.Redact(sourceUrl)), Loc.T("OpenExeTitle"), MessageBoxImage.Warning))
                return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open failed", ex); }
    }

    /// <summary>Explorer with the file selected, or its folder when the file is gone.</summary>
    public static void SelectInExplorer(string path)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (File.Exists(path)) Process.Start(explorer, "/select,\"" + path + "\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start(explorer, "\"" + dir + "\"");
        }
        catch (Exception ex) { Log.Error("Open folder failed", ex); }
    }

    public static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\"");
        }
        catch (Exception ex) { Log.Error("Open folder failed", ex); }
    }

    // ---------------- exit ----------------

    public async Task ExitAsync(bool ask)
    {
        if (_exiting) return;
        var active = Manager.ActiveCount;
        if (ask && active > 0 && !Ui.Confirm(PdmWindow.VisibleOwner(), Loc.F("ExitWhileActive", active), "PDM")) return;
        _exiting = true;
        Log.Info("Exiting");
        _scheduleTimer?.Stop();
        SetClipboardMonitor(false);
        try { await Manager.ShutdownAsync(); } catch (Exception ex) { Log.Error("Shutdown", ex); }
        Manager.Dispose();
        _pipe?.Dispose();
        Tray.Dispose();
        Main.AllowClose = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off: save progress so downloads resume next time.
        try { Manager.SaveNow(); } catch { }
        base.OnSessionEnding(e);
    }
}

internal static class WindowExtensions
{
    public static void ShowAndActivate(this Window w)
    {
        w.Show();
        Native.BringToFront(w);
    }
}
