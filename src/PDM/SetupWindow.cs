using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PDM.Core;
using PDM.Core.Ipc;

namespace PDM;

/// <summary>Shown when the exe is named "...Setup..." or started with --install.</summary>
internal sealed class SetupWindow : PdmWindow
{
    public static int Run()
    {
        AppPaths.EnsureDirectories();
        Log.Init(AppPaths.LogDir);
        var settings = Store.LoadSettings();
        Loc.Init(settings.Language);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Theme.Apply(app, settings.Theme);
        var w = new SetupWindow();
        app.MainWindow = w;
        return app.Run(w);
    }

    private readonly CheckBox _desktop;
    private readonly CheckBox _startup;
    private readonly CheckBox _opera;
    private readonly StackPanel _options = new();
    private readonly StackPanel _done = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _install;

    private SetupWindow()
    {
        Title = Loc.T("SetupTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _desktop = Ui.Check(Loc.T("SetupDesktop"), true);
        _startup = Ui.Check(Loc.T("StartWithWindows"), false);
        _opera = Ui.Check(Loc.T("SetupOpera"), true);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Ui.Text("PDM " + AppPaths.Version, 20, true));
        root.Children.Add(Ui.Text(Loc.T("SetupIntro"), wrap: true));
        var loc = new TextBox { Text = AppPaths.InstallDir, IsReadOnly = true, Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 8) };
        _options.Children.Add(Ui.Text(Loc.T("SetupLocation"), 12, brush: Theme.SubFg));
        _options.Children.Add(loc);
        _options.Children.Add(_desktop);
        _options.Children.Add(_startup);
        _options.Children.Add(_opera);
        root.Children.Add(Ui.Card(_options, new Thickness(0, 12, 0, 0)));
        root.Children.Add(_status);

        _install = Ui.Button(Loc.T("SetupInstall"), (_, _) => _ = InstallAsync(), isDefault: true);
        var buttons = Ui.Buttons(_install, Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isCancel: true));
        root.Children.Add(buttons);

        _done.Children.Add(Ui.Text(Loc.T("SetupDone"), 15, true, Theme.Good));
        _done.Children.Add(Ui.Buttons(
            Ui.Button(Loc.T("HowToInstallExt"), (_, _) => new ExtensionGuideWindow { Owner = this }.ShowDialog()),
            Ui.Button(Loc.T("SetupLaunch"), (_, _) => Launch(), isDefault: true),
            Ui.Button(Loc.T("Close"), (_, _) => Close())));
        root.Children.Add(_done);
        Content = root;
        _doneButtons = buttons;
    }

    private readonly StackPanel _doneButtons;

    private async Task InstallAsync()
    {
        _install.IsEnabled = false;
        _options.IsEnabled = false;
        _status.Text = "…";
        bool desktop = _desktop.IsChecked == true, startup = _startup.IsChecked == true, opera = _opera.IsChecked == true;
        try
        {
            await Task.Run(() => Installer.Install(desktop, startup, opera));
            _status.Text = AppPaths.InstallDir;
            _doneButtons.Visibility = Visibility.Collapsed;
            _done.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Setup failed", ex);
            _status.Text = Loc.F("SetupFailed", ex.Message);
            _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Bad);
            _install.IsEnabled = true;
            _options.IsEnabled = true;
        }
    }

    private void Launch()
    {
        try { Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = false, WorkingDirectory = AppPaths.InstallDir }); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        Close();
    }
}

internal sealed class ExtensionGuideWindow : PdmWindow
{
    public ExtensionGuideWindow()
    {
        Title = Loc.T("ExtGuideTitle");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dir = PdmApp.ExtensionDir;
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBox
        {
            Text = Loc.F("ExtGuide", dir, ExtensionInfo.Id),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
        });
        // One button per installed browser: opens its extensions page (and copies the address as a fallback).
        var browsers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (label, address, exes) in Browsers())
        {
            var exe = exes.FirstOrDefault(System.IO.File.Exists);
            if (exe == null) continue;
            var b = Ui.Button(label, (_, _) => OpenExtensionsPage(exe, address));
            b.Margin = new Thickness(browsers.Children.Count == 0 ? 0 : 6, 0, 0, 0);
            b.ToolTip = address;
            browsers.Children.Add(b);
        }
        // Firefox installs the add-on from its .xpi file instead of a folder.
        if (PdmApp.FindFirefox() != null)
        {
            var ff = Ui.Button(Loc.T("AddToFirefox"), (_, _) => ((PdmApp)Application.Current).InstallFirefoxExtension());
            ff.Margin = new Thickness(browsers.Children.Count == 0 ? 0 : 6, 0, 0, 0);
            ff.ToolTip = PdmApp.FirefoxXpiPath;
            browsers.Children.Add(ff);
        }
        if (browsers.Children.Count > 0) root.Children.Add(browsers);
        root.Children.Add(Ui.Buttons(
            Ui.Button(Loc.T("OpenExtFolder"), (_, _) => PdmApp.OpenPath(dir)),
            Ui.Button(Loc.T("CopyPath"), (_, _) => { try { Clipboard.SetText(dir); } catch { } }),
            Ui.Button(Loc.T("Close"), (_, _) => Close(), isCancel: true)));
        Content = root;
        if (root.Children[0] is TextBox t) t.SetResourceReference(TextBox.ForegroundProperty, Theme.Fg);
    }

    private static IEnumerable<(string Label, string Address, string[] Exes)> Browsers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string P(params string[] parts) => System.IO.Path.Combine(parts);
        yield return (Loc.T("OpenOperaExt"), "opera://extensions", [P(local, "Programs", "Opera", "opera.exe"), P(local, "Programs", "Opera", "launcher.exe"), P(local, "Programs", "Opera GX", "opera.exe")]);
        yield return (Loc.T("OpenChromeExt"), "chrome://extensions", [P(pf, "Google", "Chrome", "Application", "chrome.exe"), P(pf86, "Google", "Chrome", "Application", "chrome.exe"), P(local, "Google", "Chrome", "Application", "chrome.exe")]);
        yield return (Loc.T("OpenEdgeExt"), "edge://extensions", [P(pf86, "Microsoft", "Edge", "Application", "msedge.exe"), P(pf, "Microsoft", "Edge", "Application", "msedge.exe")]);
    }

    /// <summary>Some browsers ignore their internal pages when started by another program, so the address is also copied.</summary>
    private static void OpenExtensionsPage(string exe, string address)
    {
        try { Clipboard.SetText(address); } catch { }
        try { Process.Start(new ProcessStartInfo(exe, address) { UseShellExecute = false }); }
        catch { }
    }
}
