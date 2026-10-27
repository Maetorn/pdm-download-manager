using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PDM.Core;
using PDM.Core.Engine;
using PDM.Core.Security;

namespace PDM;

/// <summary>Site grabber: reads a page (and the pages it links to, up to a depth) and lists the files found.</summary>
internal sealed class GrabberWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly TextBox _url = new() { Padding = new Thickness(4) };
    private readonly ComboBox _depth = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _sameSite;
    private readonly TextBox _types = new() { Padding = new Thickness(4) };
    private readonly TextBox _maxPages = new() { Width = 90, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, Text = "200" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _busy = new() { Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _start;
    private CancellationTokenSource? _cts;
    private bool _closed;

    public GrabberWindow(PdmApp app, string? url = null)
    {
        _app = app;
        Title = Loc.T("Grabber");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (url == null)
        {
            try { url = MainWindow.ExtractLinks(Clipboard.GetText()).FirstOrDefault(); } catch { }
        }
        _url.Text = url ?? "";
        for (var d = 0; d <= 3; d++) _depth.Items.Add(new ComboBoxItem { Content = Loc.T("GrabDepth" + d), Tag = d });
        _depth.SelectedIndex = 1;
        _sameSite = Ui.Check(Loc.T("GrabSameSite"), true);
        _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Field(Loc.T("GrabStart"), _url, 150));
        root.Children.Add(Ui.Field(Loc.T("GrabDepth"), _depth, 150));
        root.Children.Add(_sameSite);
        root.Children.Add(Ui.Field(Loc.T("GrabTypes"), _types, 150));
        root.Children.Add(Ui.Text(Loc.T("GrabTypesHint"), 12, brush: Theme.SubFg, wrap: true));
        root.Children.Add(Ui.Field(Loc.T("GrabMaxPages"), _maxPages, 150));
        root.Children.Add(_busy);
        root.Children.Add(_status);
        _start = Ui.Button(Loc.T("GrabRun"), (_, _) => _ = RunAsync(), isDefault: true);
        root.Children.Add(Ui.Buttons(_start, Ui.Button(Loc.T("Close"), (_, _) => Close(), isCancel: true)));
        Content = root;
        Closed += (_, _) => { _closed = true; _cts?.Cancel(); };
        Loaded += (_, _) => { _url.Focus(); _url.SelectAll(); };
    }

    private async Task RunAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; } // the button stops a running scan
        if (!FileNames.IsHttpUrl(_url.Text.Trim(), out _)) { _status.Text = Loc.T("InvalidUrl"); _url.Focus(); return; }
        if (!int.TryParse(_maxPages.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxPages) || maxPages < 1) maxPages = 200;
        var options = new GrabberOptions
        {
            StartUrl = _url.Text.Trim(),
            Depth = _depth.SelectedItem is ComboBoxItem { Tag: int d } ? d : 1,
            SameSite = _sameSite.IsChecked == true,
            MaxPages = Math.Min(maxPages, 2000),
            Extensions = _types.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).ToList(),
            UserAgent = _app.Settings.CustomUserAgent,
        };
        var cts = _cts = new CancellationTokenSource();
        _start.Content = Loc.T("GrabStop");
        _busy.Visibility = Visibility.Visible;
        _status.Text = Loc.F("GrabProgress", 0, 0);
        var grabber = new SiteGrabber { Progress = (pages, files) => Dispatcher.BeginInvoke(() => { if (_cts == cts) _status.Text = Loc.F("GrabProgress", pages, files); }) };
        try
        {
            var files = await Task.Run(() => grabber.RunAsync(options, cts.Token));
            if (_closed) return;
            if (files.Count == 0) { _status.Text = Loc.T(cts.IsCancellationRequested ? "GrabStopped" : "GrabNone"); return; }
            var requests = files.Select(f => new AddRequest { Url = f.Url, Referrer = f.FoundOn, PageUrl = f.FoundOn }).ToList();
            if (requests.Count == 1) new AddDownloadWindow(_app, requests[0]).ShowAndActivate();
            else new AddDownloadWindow(_app, requests).ShowAndActivate();
            Close();
        }
        catch (Exception ex) { _status.Text = Loc.F("ErrUnknown", ex.Message); }
        finally
        {
            if (_cts == cts) _cts = null;
            cts.Dispose();
            _start.Content = Loc.T("GrabRun");
            _busy.Visibility = Visibility.Collapsed;
        }
    }
}
