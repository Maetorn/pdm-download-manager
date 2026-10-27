using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PDM.Core;
using PDM.Core.Engine;
using PDM.Core.Ipc;
using PDM.Core.Models;

namespace PDM;

internal sealed class SettingsWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly AppSettings _s;

    // general
    private readonly ComboBox _lang = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _theme = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _startWin, _tray, _notify, _clip, _progressWin, _completeWin, _preventSleep, _autoRetry;
    // files
    private readonly TextBox _folder = new() { Padding = new Thickness(4) };
    private readonly CheckBox _catFolders, _autoRename, _autoExtract, _deleteArchive;
    // connection
    private readonly TextBox _conns = Num(), _maxConc = Num(), _limit = Num(), _retries = Num(), _timeout = Num();
    private readonly TextBox _ua = new() { Padding = new Thickness(4) };
    private readonly ComboBox _proxyMode = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _proxyAddr = new() { Padding = new Thickness(4) };
    private readonly TextBox _proxyUser = new() { Padding = new Thickness(4) };
    private readonly PasswordBox _proxyPass = new() { Padding = new Thickness(4) };
    // site logins (edited copy, saved with Save)
    private readonly List<SiteLogin> _logins;
    private readonly ListBox _loginList = new() { Height = 220 };
    // browser
    private readonly CheckBox _dialog, _cookies;
    private readonly TextBox _origins = new() { AcceptsReturn = true, Height = 60, Padding = new Thickness(4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _regStatus = new();
    // security
    private readonly CheckBox _scan, _warnExe;
    // video
    private readonly ComboBox _quality = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _container = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _toolStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _toolProgress = new() { Height = 8, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };

    private static TextBox Num() => new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4) };

    public SettingsWindow(PdmApp app)
    {
        _app = app;
        _s = app.Settings;
        _logins = _s.Logins.Select(l => new SiteLogin { Host = l.Host, User = l.User, ProtectedPassword = l.ProtectedPassword }).ToList();
        Title = Loc.T("Settings");
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _startWin = Ui.Check(Loc.T("StartWithWindows"), _s.StartWithWindows);
        _tray = Ui.Check(Loc.T("MinimizeToTray"), _s.MinimizeToTray);
        _notify = Ui.Check(Loc.T("Notify"), _s.NotifyOnComplete);
        _clip = Ui.Check(Loc.T("ClipboardMonitor"), _s.ClipboardMonitor);
        _progressWin = Ui.Check(Loc.T("ShowProgressWindow"), _s.ShowProgressWindow);
        _preventSleep = Ui.Check(Loc.T("PreventSleep"), _s.PreventSleep);
        _autoRetry = Ui.Check(Loc.T("AutoRetry"), _s.AutoRetry);
        _completeWin = Ui.Check(Loc.T("ShowCompleteDialog"), _s.ShowCompleteDialog);
        _catFolders = Ui.Check(Loc.T("CategoryFolders"), _s.UseCategoryFolders);
        _autoRename = Ui.Check(Loc.T("AutoRename"), _s.AutoRenameDuplicates);
        _autoExtract = Ui.Check(Loc.T("AutoExtract"), _s.AutoExtract);
        _deleteArchive = Ui.Check(Loc.T("DeleteArchive"), _s.DeleteArchiveAfterExtract);
        _deleteArchive.Margin = new Thickness(22, 2, 0, 2);
        _deleteArchive.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = _autoExtract });
        _dialog = Ui.Check(Loc.T("ShowDialogBrowser"), _s.ShowDialogForBrowserDownloads);
        _cookies = Ui.Check(Loc.T("AcceptCookies"), _s.AcceptBrowserCookies);
        _scan = Ui.Check(Loc.T("ScanFiles"), _s.ScanWithAttachmentServices);
        _warnExe = Ui.Check(Loc.T("WarnExe"), _s.WarnBeforeOpeningExecutables);

        var tabs = new TabControl { Margin = new Thickness(12, 12, 12, 0) };
        tabs.Items.Add(Tab("SetGeneral", General()));
        tabs.Items.Add(Tab("SetFiles", Files()));
        tabs.Items.Add(Tab("SetConnection", Connection()));
        tabs.Items.Add(Tab("SetLogins", Logins()));
        tabs.Items.Add(Tab("SetBrowser", Browser()));
        tabs.Items.Add(Tab("SetSecurity", Security()));
        tabs.Items.Add(Tab("SetVideo", Video()));
        tabs.Items.Add(Tab("SetUpdate", Updates()));
        tabs.Items.Add(Tab("SetAbout", About()));

        var root = new DockPanel();
        var buttons = Ui.Buttons(Ui.Button(Loc.T("Save"), (_, _) => Save(), isDefault: true), Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isCancel: true));
        buttons.Margin = new Thickness(12);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    private static TabItem Tab(string key, UIElement content) => new()
    {
        Header = Loc.T(key),
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) },
    };

    private static StackPanel Panel(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    private UIElement General()
    {
        _lang.Items.Add(new ComboBoxItem { Content = Loc.T("LangAuto"), Tag = "auto" });
        foreach (var (code, name) in Loc.Languages) _lang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        _lang.SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == _s.Language) + 1);
        foreach (var (k, v) in new[] { ("ThemeSystem", "System"), ("ThemeLight", "Light"), ("ThemeDark", "Dark") })
            _theme.Items.Add(new ComboBoxItem { Content = Loc.T(k), Tag = v });
        _theme.SelectedIndex = _s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        return Panel(Ui.Field(Loc.T("Language"), _lang, 220), Ui.Field(Loc.T("Theme"), _theme, 220), _startWin, _tray, _notify, _progressWin, _completeWin, _clip, _preventSleep, _autoRetry);
    }

    private UIElement Files()
    {
        _folder.Text = _s.DownloadFolder;
        var browse = Ui.Button(Loc.T("Browse"), (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : AppSettings.DefaultDownloadFolder() };
            if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
        });
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_folder);
        return Panel(Ui.Text(Loc.T("DefaultFolder")), row, _catFolders, _autoRename, _autoExtract, _deleteArchive,
            Ui.Text(Loc.T("AutoExtractHint"), 12, brush: Theme.SubFg, wrap: true));
    }

    private UIElement Connection()
    {
        _conns.Text = _s.DefaultConnections.ToString(CultureInfo.InvariantCulture);
        _maxConc.Text = _s.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        _limit.Text = _s.SpeedLimitKBps.ToString(CultureInfo.InvariantCulture);
        _retries.Text = _s.MaxRetries.ToString(CultureInfo.InvariantCulture);
        _timeout.Text = _s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _ua.Text = _s.CustomUserAgent;
        return Panel(
            Ui.Field(Loc.T("DefaultConnections") + " (1–16)", _conns, 280),
            Ui.Field(Loc.T("MaxConcurrent") + " (1–10)", _maxConc, 280),
            Ui.Field(Loc.T("SpeedLimit"), _limit, 280),
            Ui.Field(Loc.T("Retries"), _retries, 280),
            Ui.Field(Loc.T("Timeout"), _timeout, 280),
            Ui.Text(Loc.T("UserAgent")), _ua,
            ProxyPanel());
    }

    private UIElement ProxyPanel()
    {
        foreach (var (key, mode) in new[] { ("ProxySystem", ProxyMode.System), ("ProxyNone", ProxyMode.None), ("ProxyCustom", ProxyMode.Custom) })
        {
            _proxyMode.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = mode });
            if (_s.ProxyMode == mode) _proxyMode.SelectedIndex = _proxyMode.Items.Count - 1;
        }
        _proxyAddr.Text = _s.ProxyAddress;
        _proxyUser.Text = _s.ProxyUser;
        var custom = new StackPanel();
        custom.Children.Add(Ui.Field(Loc.T("ProxyAddress"), _proxyAddr, 160));
        custom.Children.Add(Ui.Field(Loc.T("LoginUser"), _proxyUser, 160));
        custom.Children.Add(Ui.Field(Loc.T("LoginPassword"), _proxyPass, 160));
        if (_s.ProtectedProxyPassword != null) custom.Children.Add(Ui.Text(Loc.T("PasswordKept"), 12, brush: Theme.SubFg, wrap: true));
        custom.Children.Add(Ui.Text(Loc.T("ProxyHint"), 12, brush: Theme.SubFg, wrap: true));
        void Update() => custom.IsEnabled = _proxyMode.SelectedItem is ComboBoxItem { Tag: ProxyMode.Custom };
        _proxyMode.SelectionChanged += (_, _) => Update();
        Update();
        var title = Ui.Text(Loc.T("Proxy"), 13, true);
        title.Margin = new Thickness(0, 14, 0, 4);
        return Panel(title, Ui.Field(Loc.T("ProxyMode"), _proxyMode, 160), custom);
    }

    private UIElement Logins()
    {
        void Fill()
        {
            _loginList.Items.Clear();
            foreach (var l in _logins.OrderBy(l => l.Host, StringComparer.Ordinal))
                _loginList.Items.Add(new ListBoxItem { Content = $"{l.Host}   —   {l.User}", Tag = l });
        }
        void AddOrReplace(SiteLogin? old, SiteLogin? changed)
        {
            if (changed == null) return;
            if (old != null) _logins.Remove(old);
            _logins.RemoveAll(l => l.Host == changed.Host);
            _logins.Add(changed);
            Fill();
        }
        SiteLogin? Current() => (_loginList.SelectedItem as ListBoxItem)?.Tag as SiteLogin;
        var add = Ui.Button(Loc.T("LoginAdd"), (_, _) => AddOrReplace(null, LoginDialog.Edit(this, null)));
        add.Margin = new Thickness(0);
        var edit = Ui.Button(Loc.T("LoginEdit"), (_, _) => { if (Current() is { } l) AddOrReplace(l, LoginDialog.Edit(this, l)); });
        var remove = Ui.Button(Loc.T("Delete"), (_, _) => { if (Current() is { } l) { _logins.Remove(l); Fill(); } });
        _loginList.MouseDoubleClick += (_, _) => { if (Current() is { } l) AddOrReplace(l, LoginDialog.Edit(this, l)); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        buttons.Children.Add(add);
        buttons.Children.Add(edit);
        buttons.Children.Add(remove);
        Fill();
        return Panel(Ui.Text(Loc.T("LoginsIntro"), wrap: true), _loginList, buttons, Ui.Text(Loc.T("LoginHint"), 12, brush: Theme.SubFg, wrap: true));
    }

    private UIElement Browser()
    {
        UpdateRegStatus();
        _origins.Text = string.Join(Environment.NewLine, _s.ExtraAllowedOrigins);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
        var reg = Ui.Button(Loc.T("Reregister"), (_, _) => { _app.ReRegister(); _regStatus.Text = "…"; Dispatcher.BeginInvoke(UpdateRegStatus, System.Windows.Threading.DispatcherPriority.ApplicationIdle); });
        reg.Margin = new Thickness(0);
        buttons.Children.Add(reg);
        buttons.Children.Add(Ui.Button(Loc.T("OpenExtFolder"), (_, _) => PdmApp.OpenPath(PdmApp.ExtensionDir)));
        buttons.Children.Add(Ui.Button(Loc.T("HowToInstallExt"), (_, _) => new ExtensionGuideWindow { Owner = this }.ShowDialog()));
        var id = new TextBox { Text = ExtensionInfo.Id, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Padding = new Thickness(4) };
        return Panel(Ui.Field(Loc.T("BrowserStatus"), _regStatus, 160), buttons, Ui.Field("ID", id, 160), _dialog, _cookies,
            Ui.Text("chrome-extension://…/ (dev)", 12, brush: Theme.SubFg), _origins);
    }

    private void UpdateRegStatus()
    {
        var ok = NativeHostRegistration.IsRegistered();
        _regStatus.Text = ok ? Loc.T("Registered") : Loc.T("NotRegistered");
        _regStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? Theme.Good : Theme.Bad);
    }

    private UIElement Security() =>
        Panel(_scan, _warnExe, Ui.Card(Ui.Text(Loc.T("SecurityInfo"), 12.5, wrap: true), new Thickness(0, 12, 0, 0)));

    private UIElement Video()
    {
        foreach (var q in new[] { "2160", "1440", "1080", "720", "480", "360", "best" })
            _quality.Items.Add(new ComboBoxItem { Content = q == "best" ? Loc.T("Best") : q + "p", Tag = q });
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.PreferredVideoQuality) ?? _quality.Items[2];
        foreach (var c in new[] { "mp4", "mkv", "webm" }) _container.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        _container.SelectedItem = _container.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.PreferredContainer) ?? _container.Items[0];
        UpdateToolStatus();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var install = Ui.Button(Loc.T("UpdateTools"), (_, _) => _ = InstallToolsAsync());
        install.Margin = new Thickness(0);
        buttons.Children.Add(install);
        buttons.Children.Add(Ui.Button(Loc.T("RemoveTools"), (_, _) => { _app.Manager.Tools.Uninstall(); UpdateToolStatus(); }));
        buttons.Children.Add(Ui.Button(Loc.T("OpenFolder"), (_, _) => PdmApp.OpenPath(AppPaths.ToolsDir)));
        return Panel(
            Ui.Field(Loc.T("PreferredQuality"), _quality, 200),
            Ui.Field(Loc.T("Container"), _container, 200),
            Ui.Card(Panel(Ui.Text(Loc.T("VideoTools"), bold: true), _toolStatus, _toolProgress, buttons, Ui.Text(Loc.T("ToolsMissing"), 12, brush: Theme.SubFg, wrap: true)), new Thickness(0, 12, 0, 0)),
            Ui.Text(Loc.T("LegalNote"), 12, brush: Theme.SubFg, wrap: true));
    }

    private async void UpdateToolStatus()
    {
        var t = _app.Manager.Tools;
        var ffmpeg = "FFmpeg: " + (t.HasFfmpeg ? Loc.T("Installed") : Loc.T("NotInstalled"));
        string Deno(string? v) => "Deno: " + (t.HasDeno ? Loc.T("Installed") + (v != null ? $" ({v})" : "") : Loc.T("NotInstalled"));
        _toolStatus.Text = "yt-dlp: " + (t.HasYtDlp ? Loc.T("Installed") : Loc.T("NotInstalled")) + "\n" + ffmpeg + "\n" + Deno(null);
        if (t.HasYtDlp)
        {
            var v = await t.GetYtDlpVersionAsync();
            var d = await t.GetDenoVersionAsync();
            if (v != null) _toolStatus.Text = $"yt-dlp: {Loc.T("Installed")} ({v})\n{ffmpeg}\n{Deno(d)}";
        }
    }

    private async System.Threading.Tasks.Task InstallToolsAsync()
    {
        _toolProgress.Visibility = Visibility.Visible;
        try
        {
            await VideoWindow.InstallToolsAsync(_app.Manager.Tools, p => { _toolProgress.Value = p; _toolStatus.Text = Loc.F("Installing", p); }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Ui.Info(this, Loc.F("ErrUnknown", ex.Message), "PDM", MessageBoxImage.Error);
        }
        _toolProgress.Visibility = Visibility.Collapsed;
        UpdateToolStatus();
    }

    private CheckBox? _autoUpdate;
    private readonly TextBlock _updStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private UIElement Updates()
    {
        _autoUpdate = Ui.Check(Loc.T("UpdAuto"), _s.AutoUpdate);
        var check = Ui.Button(Loc.T("UpdCheckNow"), async (_, _) => await _app.Updates.CheckAsync(interactive: true, this, s => _updStatus.Text = s));
        check.Margin = new Thickness(0, 8, 0, 0);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        _updStatus.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);
        return Panel(
            Ui.Field(Loc.T("UpdVersion"), Ui.Text(AppPaths.Version, bold: true), 200),
            _autoUpdate,
            check,
            _updStatus,
            Ui.Card(Ui.Text(Loc.T("UpdHelp"), 12.5, wrap: true), new Thickness(0, 12, 0, 0)));
    }

    private UIElement About()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var data = Ui.Button(Loc.T("DataFolder"), (_, _) => PdmApp.OpenPath(AppPaths.DataRoot));
        data.Margin = new Thickness(0);
        buttons.Children.Add(data);
        buttons.Children.Add(Ui.Button(Loc.T("Logs"), (_, _) => PdmApp.OpenPath(AppPaths.LogDir)));
        buttons.Children.Add(Ui.Button(Loc.T("Uninstall"), (_, _) => Installer.StartUninstallFromApp(_app)));
        return Panel(Ui.Text(Loc.F("AboutText", AppPaths.Version), wrap: true), buttons);
    }

    private static int ParseInt(TextBox t, int fallback) =>
        int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void Save()
    {
        var folder = _folder.Text.Trim();
        if (!Path.IsPathFullyQualified(folder)) { Ui.Info(this, Loc.T("DefaultFolder") + ": ?"); return; }
        var origins = _origins.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
        var bad = origins.FirstOrDefault(o => !PdmApp.IsValidOrigin(o));
        if (bad != null) { Ui.Info(this, "chrome-extension://<32 a-p>/ : " + bad); return; }

        var proxyMode = _proxyMode.SelectedItem is ComboBoxItem { Tag: ProxyMode pm } ? pm : ProxyMode.System;
        if (proxyMode == ProxyMode.Custom && NetConfig.ParseProxy(_proxyAddr.Text) == null) { Ui.Info(this, Loc.T("ProxyBad")); return; }

        var reRegister = !origins.SequenceEqual(_s.ExtraAllowedOrigins);
        _s.Language = (string)((ComboBoxItem)_lang.SelectedItem).Tag;
        _s.Theme = (string)((ComboBoxItem)_theme.SelectedItem).Tag;
        _s.StartWithWindows = _startWin.IsChecked == true;
        _s.MinimizeToTray = _tray.IsChecked == true;
        _s.NotifyOnComplete = _notify.IsChecked == true;
        _s.ClipboardMonitor = _clip.IsChecked == true;
        _s.ShowProgressWindow = _progressWin.IsChecked == true;
        _s.ShowCompleteDialog = _completeWin.IsChecked == true;
        _s.PreventSleep = _preventSleep.IsChecked == true;
        _s.AutoRetry = _autoRetry.IsChecked == true;
        _s.DownloadFolder = folder;
        _s.UseCategoryFolders = _catFolders.IsChecked == true;
        _s.AutoRenameDuplicates = _autoRename.IsChecked == true;
        _s.AutoExtract = _autoExtract.IsChecked == true;
        _s.DeleteArchiveAfterExtract = _deleteArchive.IsChecked == true;
        _s.DefaultConnections = ParseInt(_conns, _s.DefaultConnections);
        _s.MaxConcurrentDownloads = ParseInt(_maxConc, _s.MaxConcurrentDownloads);
        _s.SpeedLimitKBps = ParseInt(_limit, _s.SpeedLimitKBps);
        _s.MaxRetries = ParseInt(_retries, _s.MaxRetries);
        _s.TimeoutSeconds = ParseInt(_timeout, _s.TimeoutSeconds);
        _s.CustomUserAgent = _ua.Text.Trim();
        _s.ProxyMode = proxyMode;
        _s.ProxyAddress = _proxyAddr.Text.Trim();
        if (_proxyUser.Text.Trim() != _s.ProxyUser) _s.ProtectedProxyPassword = null; // another user: the old password does not belong to it
        _s.ProxyUser = _proxyUser.Text.Trim();
        if (_proxyPass.Password.Length > 0) _s.ProtectedProxyPassword = Core.Security.Dpapi.Protect(_proxyPass.Password);
        if (_s.ProxyUser.Length == 0) _s.ProtectedProxyPassword = null;
        _s.Logins = _logins;
        _s.ShowDialogForBrowserDownloads = _dialog.IsChecked == true;
        _s.AcceptBrowserCookies = _cookies.IsChecked == true;
        _s.ExtraAllowedOrigins = origins;
        _s.ScanWithAttachmentServices = _scan.IsChecked == true;
        _s.WarnBeforeOpeningExecutables = _warnExe.IsChecked == true;
        _s.PreferredVideoQuality = (string)((ComboBoxItem)_quality.SelectedItem).Tag;
        _s.PreferredContainer = (string)((ComboBoxItem)_container.SelectedItem).Tag;
        _s.AutoUpdate = _autoUpdate?.IsChecked == true;
        _app.SaveSettings();
        if (reRegister) _app.ReRegister();
        Close();
    }
}
