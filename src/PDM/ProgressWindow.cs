using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PDM.Core.Engine;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM;

/// <summary>Map of the file: downloaded parts in the accent colour, a thin marker at each connection's position.</summary>
internal sealed class SegmentBar : Canvas
{
    public SegmentBar()
    {
        Height = 18;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, Theme.Track);
    }

    public void Update(DownloadItem item, DownloadManager manager)
    {
        Children.Clear();
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0) return;
        var accent = (Brush)FindResource(Theme.Accent);
        if (item.Status == DownloadStatus.Completed)
        {
            Children.Add(new Rectangle { Width = w, Height = h, Fill = accent });
            return;
        }
        var total = item.TotalBytes;
        if (total <= 0)
        {
            return;
        }
        var segs = manager.LiveSegments(item) ?? item.Segments;
        if (segs.Count == 0 && item.DownloadedBytes > 0)
        {
            // Single stream without saved segments: one bar from the start.
            Children.Add(new Rectangle { Width = Math.Min(w, item.DownloadedBytes * w / total), Height = h, Fill = accent });
            return;
        }
        var edge = (Brush)FindResource(Theme.Fg);
        foreach (var s in segs)
        {
            var x = s.Start * w / total;
            var done = Math.Max(0, Math.Min(s.Position, s.End + 1) - s.Start);
            var dw = done * w / total;
            if (dw > 0.5)
            {
                var r = new Rectangle { Width = dw, Height = h, Fill = accent };
                SetLeft(r, x);
                Children.Add(r);
            }
            if (!s.IsDone)
            {
                var m = new Rectangle { Width = 1.5, Height = h, Fill = edge, Opacity = 0.6 };
                SetLeft(m, x + dw);
                Children.Add(m);
            }
        }
    }
}

/// <summary>IDM-style window shown while one download runs: live numbers, progress, segments, pause/cancel.</summary>
internal sealed class ProgressWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly DownloadItem _item;
    private readonly TextBlock _url = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _size = new();
    private readonly TextBlock _downloaded = new();
    private readonly TextBlock _speed = new();
    private readonly TextBlock _eta = new();
    private readonly TextBlock _resume = new();
    private readonly ProgressBar _progress = new() { Height = 16, Maximum = 100, Margin = new Thickness(0, 10, 0, 6) };
    private readonly SegmentBar _segments = new();
    private readonly Button _pause;
    private readonly StackPanel _details = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<ConnRow> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public DownloadItem Item => _item;

    /// <summary>One line of the connection table (updated in place so the grid does not flicker).</summary>
    internal sealed class ConnRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _downloaded = "", _info = "";
        public string No { get; init; } = "";
        public string Downloaded { get => _downloaded; set { if (_downloaded != value) { _downloaded = value; Changed(nameof(Downloaded)); } } }
        public string Info { get => _info; set { if (_info != value) { _info = value; Changed(nameof(Info)); } } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public ProgressWindow(PdmApp app, DownloadItem item)
    {
        _app = app;
        _item = item;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        var name = Ui.Text(item.FileName, 14, true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DownloadItem.FileName)) { Source = item });
        root.Children.Add(name);
        _url.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);
        _url.Margin = new Thickness(0, 2, 0, 8);
        root.Children.Add(_url);
        root.Children.Add(Ui.Field(Loc.T("Status"), _status, 170));
        root.Children.Add(Ui.Field(Loc.T("FileSize"), _size, 170));
        root.Children.Add(Ui.Field(Loc.T("Downloaded"), _downloaded, 170));
        root.Children.Add(Ui.Field(Loc.T("Speed"), _speed, 170));
        root.Children.Add(Ui.Field(Loc.T("TimeLeft"), _eta, 170));
        root.Children.Add(Ui.Field(Loc.T("ResumeCapability"), _resume, 170));
        root.Children.Add(Ui.Field(Loc.T("SpeedLimitToggle"), SpeedLimitRow(), 170));
        if (app.Settings.SpeedLimitKBps > 0)
        {
            // The global limit caps every download: say so here, where the speed is looked at.
            var warn = Ui.Text(Loc.F("GlobalLimitActive", Ui.Size(app.Settings.SpeedLimitKBps * 1024L)), 12, brush: Theme.Bad, wrap: true);
            warn.Margin = new Thickness(0, 0, 0, 6);
            root.Children.Add(warn);
        }
        root.Children.Add(_progress);

        // Buttons like IDM: details toggle on the left, Pause / Cancel on the right.
        _pause = Ui.Button(Loc.T("Pause"), (_, _) => TogglePause());
        var toggle = Ui.Button("<< " + Loc.T("HideDetails"));
        toggle.Margin = new Thickness(0);
        toggle.Click += (_, _) =>
        {
            var show = _details.Visibility != Visibility.Visible;
            _details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = show ? "<< " + Loc.T("HideDetails") : Loc.T("ShowDetails") + " >>";
        };
        var buttonRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        DockPanel.SetDock(toggle, Dock.Left);
        buttonRow.Children.Add(toggle);
        var right = Ui.Buttons(_pause, Ui.Button(Loc.T("Cancel"), (_, _) => { if (_item.IsActive || _item.Status == DownloadStatus.Queued) _app.Manager.Pause(_item); Close(); }));
        right.Margin = new Thickness(0);
        buttonRow.Children.Add(right);
        root.Children.Add(buttonRow);

        // Details: segment map and one row per connection.
        var segTitle = Ui.Text(Loc.T("SegmentsTitle"), 12, brush: Theme.SubFg);
        segTitle.HorizontalAlignment = HorizontalAlignment.Center;
        segTitle.Margin = new Thickness(0, 0, 0, 4);
        _details.Children.Add(segTitle);
        var segBorder = new Border { Child = _segments, BorderThickness = new Thickness(1) };
        segBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
        _details.Children.Add(segBorder);
        var grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            CanUserSortColumns = false,
            CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            Height = 150,
            Margin = new Thickness(0, 8, 0, 0),
            RowHeight = 20,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("ConnNo"), Binding = new System.Windows.Data.Binding(nameof(ConnRow.No)), Width = 44 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Downloaded"), Binding = new System.Windows.Data.Binding(nameof(ConnRow.Downloaded)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("ConnInfo"), Binding = new System.Windows.Data.Binding(nameof(ConnRow.Info)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _details.Children.Add(grid);
        root.Children.Add(_details);

        Content = root;
        // Esc only hides this window; the download keeps running.
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _segments.SizeChanged += (_, _) => _segments.Update(_item, _app.Manager);
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    /// <summary>IDM's "Speed Limiter": a per-download limit that applies at once.</summary>
    private StackPanel SpeedLimitRow()
    {
        var last = _item.SpeedLimitKBps > 0 ? _item.SpeedLimitKBps : Math.Max(1, _app.Manager.Settings.LastSpeedLimitKBps);
        var on = Ui.Check(Loc.T("LimitThisDownload"), _item.SpeedLimitKBps > 0);
        on.VerticalAlignment = VerticalAlignment.Center;
        var box = new TextBox { Width = 70, Margin = new Thickness(10, 0, 4, 0), Text = last.ToString(CultureInfo.InvariantCulture) };
        void Apply()
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) || kbps <= 0)
            {
                box.Text = last.ToString(CultureInfo.InvariantCulture);
                kbps = last;
            }
            last = kbps;
            _app.Manager.SetSpeedLimit(_item, on.IsChecked == true ? kbps : 0);
        }
        on.Checked += (_, _) => Apply();
        on.Unchecked += (_, _) => Apply();
        box.LostFocus += (_, _) => { if (on.IsChecked == true) Apply(); };
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && on.IsChecked == true) Apply(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(on);
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "KB/s", VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void TogglePause()
    {
        if (_item.IsActive || _item.Status == DownloadStatus.Queued) _app.Manager.Pause(_item);
        else if (_item.Status != DownloadStatus.Completed) _app.Manager.StartNow(_item);
        Refresh();
    }

    private void Refresh()
    {
        var i = _item;
        var pct = i.Progress;
        Title = i.TotalBytes > 0 ? $"{pct:0}% {i.FileName}" : i.FileName;
        _url.Text = FileNames.Redact(i.FinalUrl ?? i.Url);
        _status.Text = i.Status switch
        {
            DownloadStatus.Error => Loc.ErrorText(i.Error),
            DownloadStatus.Downloading when i.ActiveConnections > 0 => $"{Loc.T("StDownloading")} ({Loc.F("ActiveConnections", i.ActiveConnections)})",
            _ => Loc.StatusText(i.Status),
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, i.Status == DownloadStatus.Error ? Theme.Bad : Theme.Fg);
        _size.Text = i.TotalBytes > 0 ? Ui.Size(i.TotalBytes) : Loc.T("SizeUnknown");
        _downloaded.Text = i.TotalBytes > 0
            ? $"{Ui.Size(i.DownloadedBytes)} ({pct.ToString("0.00", CultureInfo.CurrentCulture)} %)"
            : Ui.Size(i.DownloadedBytes);
        _speed.Text = Ui.Speed(i.Speed);
        _eta.Text = Ui.Eta(i.Eta);
        // Video jobs keep their parts and progress (PDM's engine state or yt-dlp's .part files), so they continue too.
        _resume.Text = i.Kind == DownloadKind.Video || i.SupportsResume ? Loc.T("Yes") : (i.Status == DownloadStatus.Connecting ? "…" : Loc.T("No"));
        _progress.IsIndeterminate = i.TotalBytes <= 0 && i.IsActive;
        _progress.Value = pct;
        var running = i.IsActive || i.Status == DownloadStatus.Queued;
        _pause.Content = running ? Loc.T("Pause") : (i.Status == DownloadStatus.Error ? Loc.T("Retry") : Loc.T("Resume"));
        _pause.IsEnabled = i.Status != DownloadStatus.Completed;
        _segments.Update(i, _app.Manager);
        if (_details.Visibility == Visibility.Visible) UpdateRows(running);
        if (i.Status == DownloadStatus.Completed || !_app.Manager.Items.Contains(i)) Close();
    }

    private void UpdateRows(bool running)
    {
        var i = _item;
        var lines = new List<(string Downloaded, string Info)>();
        var segs = _app.Manager.LiveSegments(i) ?? (i.Kind == DownloadKind.Http ? i.Segments : null);
        if (segs is { Count: > 0 })
        {
            foreach (var s in segs)
            {
                var done = Math.Max(0, Math.Min(s.Position, s.End + 1) - s.Start);
                var info = s.IsDone ? Loc.T("StCompleted")
                    : !running ? Loc.StatusText(i.Status == DownloadStatus.Error ? DownloadStatus.Error : DownloadStatus.Paused)
                    : i.Status == DownloadStatus.Connecting ? Loc.T("StConnecting")
                    : i.StatusDetail == "retry" ? Loc.T("StRetry")
                    : Loc.T("ConnReceiving");
                lines.Add((Ui.Size(done), info));
            }
        }
        else
        {
            // Single stream (no ranges) or a yt-dlp job.
            var info = i.StatusDetail switch
            {
                "merge" => Loc.T("StMerge"),
                "convert" => Loc.T("StConvert"),
                "retry" => Loc.T("StRetry"),
                _ => running ? (i.Status == DownloadStatus.Connecting ? Loc.T("StConnecting") : Loc.T("ConnReceiving")) : Loc.StatusText(i.Status),
            };
            lines.Add((Ui.Size(i.DownloadedBytes), info));
        }
        while (_rows.Count > lines.Count) _rows.RemoveAt(_rows.Count - 1);
        for (var n = 0; n < lines.Count; n++)
        {
            if (n == _rows.Count) _rows.Add(new ConnRow { No = (n + 1).ToString(CultureInfo.CurrentCulture) });
            _rows[n].Downloaded = lines[n].Downloaded;
            _rows[n].Info = lines[n].Info;
        }
    }
}

/// <summary>"Download complete" window: Open / Open with / Open folder / Close, with "don't show again".</summary>
internal sealed class CompleteWindow : PdmWindow
{
    public CompleteWindow(PdmApp app, DownloadItem item)
    {
        Title = Loc.T("NotifyDone");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;

        var root = new StackPanel { Margin = new Thickness(16) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Source = Native.IconForExtension(item.FileName, large: true) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(Loc.T("NotifyDone"), 14, true));
        var bytes = item.TotalBytes > 0 ? item.TotalBytes : TryLength(item.FullPath);
        texts.Children.Add(Ui.Text(Loc.F("DownloadedBytes", Ui.Size(bytes), bytes.ToString("N0", CultureInfo.CurrentCulture)), brush: Theme.SubFg));
        head.Children.Add(texts);
        root.Children.Add(head);

        root.Children.Add(Ui.Text(Loc.T("Url"), 12, brush: Theme.SubFg));
        root.Children.Add(ReadOnlyBox(FileNames.Redact(item.FinalUrl ?? item.Url)));
        root.Children.Add(Ui.Text(Loc.T("SavedAs"), 12, brush: Theme.SubFg));
        root.Children.Add(ReadOnlyBox(item.FullPath));

        var dontShow = Ui.Check(Loc.T("DontShowAgain"), false);
        var open = Ui.Button(Loc.T("Open"), (_, _) => { Close(); app.Open(item); }, isDefault: true);
        open.Margin = new Thickness(0);
        var buttons = Ui.Buttons(
            open,
            Ui.Button(Loc.T("OpenWith"), (_, _) => { Close(); OpenWith(item.FullPath); }),
            Ui.Button(Loc.T("OpenFolder"), (_, _) => { Close(); app.OpenFolder(item); }),
            Ui.Button(Loc.T("Close"), (_, _) => Close(), isCancel: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Stretch;
        root.Children.Add(buttons);
        root.Children.Add(dontShow);
        Content = root;

        Closed += (_, _) =>
        {
            if (dontShow.IsChecked != true) return;
            app.Settings.ShowCompleteDialog = false;
            app.SaveSettings();
        };
    }

    private static long TryLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private static TextBox ReadOnlyBox(string text) => new() { Text = text, IsReadOnly = true, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 8) };

    /// <summary>Windows' own "Open with" chooser. Nothing is run until the user picks an app there.</summary>
    private static void OpenWith(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            // OpenAs_RunDLL takes the rest of the command line as the path, unquoted.
            Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "rundll32.exe"), "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false });
        }
        catch (Exception ex) { Core.Log.Error("Open with failed", ex); }
    }
}
