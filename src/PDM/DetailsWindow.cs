using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PDM.Core.Engine;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM;

/// <summary>Live details of one download: segment map, connections, refresh address, SHA-256.</summary>
internal sealed class DetailsWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly DownloadItem _item;
    private readonly SegmentBar _segments = new() { Height = 22 };
    private readonly TextBlock _status = new();
    private readonly TextBlock _downloaded = new();
    private readonly TextBlock _speed = new();
    private readonly TextBlock _eta = new();
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _url = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Slider _conn = new() { Minimum = 1, Maximum = 16, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 220 };
    private readonly TextBlock _connText = new() { Margin = new Thickness(8, 0, 0, 0), Width = 30 };
    private readonly TextBox _hash = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(4) };
    private readonly TextBox _expected = new() { FontFamily = new FontFamily("Consolas"), Padding = new Thickness(4) };
    private readonly TextBlock _verify = new() { Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _pauseResume;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _hashCts;

    public DetailsWindow(PdmApp app, DownloadItem item)
    {
        _app = app;
        _item = item;
        Title = Loc.T("DetailsTitle") + " — " + item.FileName;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Text(item.FileName, 15, true));
        root.Children.Add(_progress);
        root.Children.Add(Ui.Field(Loc.T("Status"), _status, 130));
        root.Children.Add(Ui.Field(Loc.T("Downloaded"), _downloaded, 130));
        root.Children.Add(Ui.Field(Loc.T("Speed"), _speed, 130));
        root.Children.Add(Ui.Field(Loc.T("TimeLeft"), _eta, 130));
        root.Children.Add(Ui.Field(Loc.T("Path"), _path, 130));
        root.Children.Add(Ui.Field(Loc.T("Url"), _url, 130));

        var segBorder = new Border { Child = _segments, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0) };
        segBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
        root.Children.Add(Ui.Field(Loc.T("Segments"), segBorder, 130));

        _conn.Value = item.Connections;
        _connText.Text = item.Connections.ToString();
        _conn.ValueChanged += (_, e) =>
        {
            var n = (int)e.NewValue;
            _connText.Text = n.ToString();
            if (n != _item.Connections) _app.Manager.SetConnections(_item, n);
        };
        var connRow = new StackPanel { Orientation = Orientation.Horizontal };
        connRow.Children.Add(_conn);
        connRow.Children.Add(_connText);
        root.Children.Add(Ui.Field(Loc.T("Connections"), connRow, 130));

        // SHA-256
        var hashCard = new StackPanel();
        var hashRow = new DockPanel();
        var compute = Ui.Button(Loc.T("ComputeSha"), (_, _) => _ = ComputeHashAsync());
        DockPanel.SetDock(compute, Dock.Right);
        hashRow.Children.Add(compute);
        hashRow.Children.Add(_hash);
        hashCard.Children.Add(hashRow);
        var verifyRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var verify = Ui.Button(Loc.T("Verify"), (_, _) => Verify());
        DockPanel.SetDock(_verify, Dock.Right);
        DockPanel.SetDock(verify, Dock.Right);
        verifyRow.Children.Add(_verify);
        verifyRow.Children.Add(verify);
        verifyRow.Children.Add(_expected);
        hashCard.Children.Add(Ui.Text(Loc.T("ExpectedSha"), 12, brush: Theme.SubFg));
        hashCard.Children.Add(verifyRow);
        _hash.Text = item.Sha256 ?? "";
        _expected.Text = item.ExpectedSha256 ?? "";
        root.Children.Add(new GroupBox { Header = "SHA-256", Content = hashCard, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0) });

        _pauseResume = Ui.Button(Loc.T("Pause"), (_, _) =>
        {
            if (_item.IsActive || _item.Status == DownloadStatus.Queued) _app.Manager.Pause(_item);
            else _app.Manager.Resume(_item);
        });
        root.Children.Add(Ui.Buttons(
            Ui.Button(Loc.T("Open"), (_, _) => _app.Open(_item)),
            Ui.Button(Loc.T("OpenFolder"), (_, _) => _app.OpenFolder(_item)),
            Ui.Button(Loc.T("RefreshUrl"), (_, _) => RefreshUrl()),
            _pauseResume,
            Ui.Button(Loc.T("Close"), (_, _) => Close(), isCancel: true)));
        Content = root;

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _segments.SizeChanged += (_, _) => _segments.Update(_item, _app.Manager);
        Refresh();
        Closed += (_, _) => { _timer.Stop(); _hashCts?.Cancel(); };
    }

    private void Refresh()
    {
        var i = _item;
        _status.Text = i.Status == DownloadStatus.Error ? Loc.ErrorText(i.Error) : Loc.StatusText(i.Status) + (i.ActiveConnections > 0 ? $" ({i.ActiveConnections})" : "");
        _downloaded.Text = i.TotalBytes > 0 ? $"{Ui.Size(i.DownloadedBytes)} / {Ui.Size(i.TotalBytes)}  ({i.Progress:0.0} %)" : Ui.Size(i.DownloadedBytes);
        _speed.Text = Ui.Speed(i.Speed);
        _eta.Text = Ui.Eta(i.Eta);
        _path.Text = i.FullPath;
        _url.Text = FileNames.Redact(i.FinalUrl ?? i.Url);
        _progress.Value = i.Progress;
        _pauseResume.Content = i.IsActive || i.Status == DownloadStatus.Queued ? Loc.T("Pause") : Loc.T("Resume");
        _pauseResume.IsEnabled = i.Status != DownloadStatus.Completed;
        _segments.Update(i, _app.Manager);
    }

    private void RefreshUrl()
    {
        var url = Ui.Prompt(this, Loc.T("RefreshUrl"), Loc.T("RefreshPrompt"), _item.Url);
        if (url == null) return;
        if (!FileNames.IsHttpUrl(url.Trim(), out _)) { Ui.Info(this, Loc.T("InvalidUrl")); return; }
        _app.Manager.RefreshUrl(_item, url.Trim());
    }

    private async System.Threading.Tasks.Task ComputeHashAsync()
    {
        var path = _item.FullPath;
        if (_item.Status != DownloadStatus.Completed || !File.Exists(path)) return;
        _hashCts?.Cancel();
        var cts = _hashCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => _hash.Text = $"{p:0} %");
        try
        {
            var hash = await System.Threading.Tasks.Task.Run(() => DownloadManager.ComputeSha256Async(path, cts.Token, progress));
            _hash.Text = hash;
            _item.Sha256 = hash;
            _app.Manager.SaveNow();
            if (_expected.Text.Trim().Length > 0) Verify();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _hash.Text = ex.Message; }
    }

    private void Verify()
    {
        var expected = _expected.Text.Trim().Replace(" ", "").ToLowerInvariant();
        var actual = (_item.Sha256 ?? _hash.Text).Trim().ToLowerInvariant();
        if (expected.Length == 0) { _verify.Text = ""; return; }
        if (actual.Length != 64) { _ = ComputeHashAsync(); return; }
        var ok = expected == actual;
        _verify.Text = ok ? Loc.T("HashMatch") : Loc.T("HashNoMatch");
        _verify.SetResourceReference(TextBlock.ForegroundProperty, ok ? Theme.Good : Theme.Bad);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _timer.Stop();
        base.OnClosing(e);
    }
}
