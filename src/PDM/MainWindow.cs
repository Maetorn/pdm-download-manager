using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDM.Core.Engine;
using PDM.Core.Ipc;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM;

internal sealed partial class MainWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly DownloadManager _m;
    private readonly ListCollectionView _view;
    private readonly DataGrid _grid;
    private readonly ListBox _cats;
    private readonly TextBox _search;
    private readonly TextBlock _statusLeft = new();
    private readonly TextBlock _statusRight = new();
    private readonly TextBlock _empty;
    private readonly Dictionary<string, TextBlock> _catCounts = new();
    private string _category = "All";
    private bool _trayHintShown;

    public bool AllowClose { get; set; }

    private static readonly string[] CategoryKeys = ["All", "Active", "Unfinished", "Finished", "Compressed", "Document", "Music", "Program", "Video", "Image", "Other"];
    private const string QueuePrefix = "queue:";

    public MainWindow(PdmApp app)
    {
        _app = app;
        _m = app.Manager;
        Title = Loc.T("AppTitle");
        Width = 1100;
        Height = 640;
        MinWidth = 720;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_m.Items);
        _view.Filter = Filter;
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(DownloadItem.Status));
        _view.LiveFilteringProperties.Add(nameof(DownloadItem.FileName));

        var root = new DockPanel();

        // ---- toolbar ----
        var toolbar = new DockPanel();
        var tbBorder = new Border { Child = toolbar, Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
        tbBorder.SetResourceReference(Border.BackgroundProperty, Theme.Panel);
        tbBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
        // Long captions (German, French …) or a narrow window: the buttons wrap into a second row instead of being cut.
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Ui.ToolButton("", Loc.T("Add"), (_, _) => AddNew()));
        tools.Children.Add(Ui.ToolButton("", Loc.T("Resume"), (_, _) => ForSelected(i => _m.Resume(i))));
        tools.Children.Add(Ui.ToolButton("", Loc.T("Pause"), (_, _) => ForSelected(i => _m.Pause(i))));
        tools.Children.Add(Ui.ToolButton("", Loc.T("StopAll"), (_, _) => _m.PauseAll()));
        tools.Children.Add(Ui.ToolButton("", Loc.T("Delete"), (_, _) => DeleteSelected()));
        tools.Children.Add(Ui.ToolButton("", Loc.T("DeleteCompleted"), (_, _) => _m.ClearCompleted()));
        tools.Children.Add(Separator());
        tools.Children.Add(Ui.ToolButton("", Loc.T("StartQueue"), (s, _) => PickQueue((Button)s, id => _m.StartQueue(id))));
        tools.Children.Add(Ui.ToolButton("", Loc.T("StopQueue"), (s, _) => PickQueue((Button)s, id => _m.StopQueue(id))));
        tools.Children.Add(Ui.ToolButton("", Loc.T("Scheduler"), (_, _) => new SchedulerWindow(_app, SelectedQueueId()) { Owner = this }.ShowDialog()));
        tools.Children.Add(Separator());
        tools.Children.Add(Ui.ToolButton("", Loc.T("Video"), (_, _) => OpenVideo()));
        tools.Children.Add(Ui.ToolButton("", Loc.T("Settings"), (_, _) => new SettingsWindow(_app) { Owner = this }.ShowDialog()));
        tools.Children.Add(Ui.ToolButton("\uE916", Loc.T("SpeedLimitToggle"), (_, _) => _app.ToggleSpeedLimit()));
        tools.Children.Add(Ui.ToolButton("\uE712", Loc.T("More"), (s, _) => ShowMoreMenu((Button)s)));

        _search = new TextBox { Width = 220, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 3, 4, 3), ToolTip = Loc.T("Search") };
        var searchHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        var placeholder = Ui.Text(Loc.T("Search"), brush: Theme.SubFg);
        placeholder.Margin = new Thickness(7, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        searchHost.Children.Add(_search);
        searchHost.Children.Add(placeholder);
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Refresh();
            UpdateEmpty();
        };
        DockPanel.SetDock(searchHost, Dock.Right);
        toolbar.Children.Add(searchHost); // docked first: it keeps its place, the buttons get the rest
        toolbar.Children.Add(tools);
        DockPanel.SetDock(tbBorder, Dock.Top);
        root.Children.Add(tbBorder);

        // ---- status bar ----
        var status = new StatusBar();
        status.Items.Add(new StatusBarItem { Content = _statusLeft });
        var right = new StatusBarItem { Content = _statusRight, HorizontalAlignment = HorizontalAlignment.Right };
        status.Items.Add(right);
        _statusLeft.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);
        _statusRight.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // ---- category sidebar ----
        _cats = new ListBox { Width = 190, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 6, 0, 0) };
        _cats.SetResourceReference(BackgroundProperty, Theme.Panel);
        _cats.SetResourceReference(BorderBrushProperty, Theme.Border);
        var header = Ui.Text(Loc.T("Categories"), 11, true, Theme.SubFg);
        header.Margin = new Thickness(10, 4, 0, 6);
        _cats.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false });
        foreach (var key in CategoryKeys)
        {
            var g = new Grid { Width = 160 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Ui.Text(Loc.T("Cat" + key));
            if (key is "Compressed" or "Document" or "Music" or "Program" or "Video" or "Image" or "Other") label.Margin = new Thickness(14, 0, 0, 0);
            g.Children.Add(label);
            var count = Ui.Text("", 11.5, brush: Theme.SubFg);
            Grid.SetColumn(count, 1);
            g.Children.Add(count);
            _catCounts[key] = count;
            _cats.Items.Add(new ListBoxItem { Content = g, Tag = key, Padding = new Thickness(8, 5, 8, 5) });
        }
        RebuildQueues();
        _cats.SelectedIndex = 1;
        _cats.SelectionChanged += (_, _) =>
        {
            if (_cats.SelectedItem is ListBoxItem { Tag: string key })
            {
                _category = key;
                _view.Refresh();
                UpdateEmpty();
            }
        };
        DockPanel.SetDock(_cats, Dock.Left);
        root.Children.Add(_cats);

        // ---- grid ----
        _grid = BuildGrid();
        _empty = Ui.Text(Loc.T("EmptyList"), 14, brush: Theme.SubFg, wrap: true);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.MaxWidth = 420;
        _empty.IsHitTestVisible = false;
        var center = new Grid();
        center.Children.Add(_grid);
        center.Children.Add(_empty);
        center.Children.Add(_bandLayer);
        _band.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
        _band.SetResourceReference(Border.BackgroundProperty, Theme.Sel);
        _bandLayer.Children.Add(_band);
        AttachRubberBand(_grid);
        root.Children.Add(center);

        Content = root;

        // ---- keys / drag-drop ----
        InputBindings.Add(new KeyBinding(new Cmd(AddNew), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Cmd(PasteLinks), Key.V, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Cmd(() => _search.Focus()), Key.F, ModifierKeys.Control));
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            var text = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
            if (text != null) AddLinks(ExtractLinks(text));
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshCounts();
        timer.Start();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshCounts(); }; // counts are skipped while hidden
        _m.Items.CollectionChanged += (_, _) => UpdateEmpty();
        RefreshCounts();
        UpdateEmpty();
    }

    private static UIElement Separator()
    {
        var r = new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8) };
        r.SetResourceReference(Border.BackgroundProperty, Theme.Border);
        return r;
    }

    // ---------------- grid ----------------

    private DataGrid BuildGrid()
    {
        var g = new DataGrid
        {
            ItemsSource = _view,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            RowHeight = 26,
            BorderThickness = new Thickness(0),
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
        VirtualizingPanel.SetIsVirtualizing(g, true);
        VirtualizingPanel.SetVirtualizationMode(g, VirtualizationMode.Recycling);

        // Name with shell icon
        var nameCol = new DataGridTemplateColumn { Header = Loc.T("ColName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180, SortMemberPath = nameof(DownloadItem.FileName) };
        var sp = new FrameworkElementFactory(typeof(DockPanel));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetValue(WidthProperty, 16.0);
        img.SetValue(HeightProperty, 16.0);
        img.SetValue(MarginProperty, new Thickness(4, 0, 6, 0));
        img.SetBinding(Image.SourceProperty, new Binding(nameof(DownloadItem.FileName)) { Converter = new FuncConverter(v => Native.IconForExtension(v as string)) });
        sp.AppendChild(img);
        var txt = new FrameworkElementFactory(typeof(TextBlock));
        txt.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadItem.FileName)));
        txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        txt.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        sp.AppendChild(txt);
        nameCol.CellTemplate = new DataTemplate { VisualTree = sp };
        g.Columns.Add(nameCol);

        g.Columns.Add(TextColumn(Loc.T("ColSize"), 80, nameof(DownloadItem.TotalBytes), v => v is long l ? Ui.Size(l) : ""));

        var statusBinding = new MultiBinding { Converter = new FuncMultiConverter(StatusText) };
        statusBinding.Bindings.Add(new Binding(nameof(DownloadItem.Status)));
        statusBinding.Bindings.Add(new Binding(nameof(DownloadItem.Error)));
        statusBinding.Bindings.Add(new Binding(nameof(DownloadItem.StatusDetail)));
        statusBinding.Bindings.Add(new Binding(nameof(DownloadItem.ActiveConnections)));
        statusBinding.Bindings.Add(new Binding(nameof(DownloadItem.RetryAt)));
        var statusCol = new DataGridTextColumn { Header = Loc.T("ColStatus"), Width = HeaderFit(Loc.T("ColStatus"), 150), Binding = statusBinding, SortMemberPath = nameof(DownloadItem.Status) };
        var st = new Style(typeof(TextBlock));
        st.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var errTrigger = new DataTrigger { Binding = new Binding(nameof(DownloadItem.Status)), Value = DownloadStatus.Error };
        errTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Theme.Bad)));
        st.Triggers.Add(errTrigger);
        var okTrigger = new DataTrigger { Binding = new Binding(nameof(DownloadItem.Status)), Value = DownloadStatus.Completed };
        okTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Theme.Good)));
        st.Triggers.Add(okTrigger);
        statusCol.ElementStyle = st;
        g.Columns.Add(statusCol);

        // Progress bar with percentage on top
        var progCol = new DataGridTemplateColumn { Header = Loc.T("ColProgress"), Width = HeaderFit(Loc.T("ColProgress"), 120), SortMemberPath = nameof(DownloadItem.Progress) };
        var pg = new FrameworkElementFactory(typeof(Grid));
        pg.SetValue(MarginProperty, new Thickness(2, 4, 6, 4));
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetValue(ProgressBar.MaximumProperty, 100.0);
        bar.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(DownloadItem.Progress)) { Mode = BindingMode.OneWay });
        pg.AppendChild(bar);
        var pct = new FrameworkElementFactory(typeof(TextBlock));
        pct.SetValue(TextBlock.FontSizeProperty, 11.0);
        pct.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        pct.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        pct.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadItem.Progress)) { Converter = new FuncConverter(v => v is double d ? d.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "") });
        pg.AppendChild(pct);
        progCol.CellTemplate = new DataTemplate { VisualTree = pg };
        g.Columns.Add(progCol);

        g.Columns.Add(TextColumn(Loc.T("ColSpeed"), 85, nameof(DownloadItem.Speed), v => v is double d ? Ui.Speed(d) : ""));
        g.Columns.Add(TextColumn(Loc.T("ColEta"), 80, nameof(DownloadItem.Eta), v => Ui.Eta(v as TimeSpan?)));
        g.Columns.Add(TextColumn(Loc.T("ColAdded"), 125, nameof(DownloadItem.CreatedAt), v => v is DateTime t ? t.ToString("g", CultureInfo.CurrentCulture) : ""));

        g.ContextMenu = BuildContextMenu();
        g.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is { Item: DownloadItem item }) Activate(item);
        };
        g.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Enter && Selected().FirstOrDefault() is { } item) { Activate(item); e.Handled = true; }
        };
        return g;
    }

    private static DataGridTextColumn TextColumn(string header, double width, string path, Func<object?, object?> convert) => new()
    {
        Header = header,
        Width = HeaderFit(header, width),
        SortMemberPath = path,
        Binding = new Binding(path) { Converter = new FuncConverter(convert) },
        ElementStyle = CenteredText(),
    };

    private static Style CenteredText()
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        s.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        s.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 6, 0)));
        return s;
    }

    /// <summary>Column width: <paramref name="width"/>, or wider when the translated header needs it (plus room for the sort arrow).</summary>
    private static double HeaderFit(string header, double width)
    {
        var text = new FormattedText(header, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1.0);
        return Math.Max(width, Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 22);
    }

    private static object StatusText(object?[] v)
    {
        if (v.Length < 4 || v[0] is not DownloadStatus s) return "";
        var error = v[1] as string;
        var detail = v[2] as string;
        if (s == DownloadStatus.Error)
            return v.Length > 4 && v[4] is DateTime retry ? Loc.ErrorText(error) + " · " + Loc.F("AutoRetryAt", retry.ToString("HH:mm:ss", CultureInfo.CurrentCulture)) : Loc.ErrorText(error);
        if (s is DownloadStatus.Downloading or DownloadStatus.Connecting)
        {
            var d = detail switch
            {
                "retry" => Loc.T("StRetry"),
                "changed" => Loc.T("StChanged"),
                "merge" => Loc.T("StMerge"),
                "convert" => Loc.T("StConvert"),
                _ => null,
            };
            if (d != null) return d;
            if (s == DownloadStatus.Downloading && v[3] is int c && c > 0) return $"{Loc.T("StDownloading")} ({c})";
        }
        return Loc.StatusText(s);
    }

    private ContextMenu BuildContextMenu()
    {
        var cm = new ContextMenu();
        MenuItem Item(string key, Action a, string? gesture = null)
        {
            var mi = new MenuItem { Header = Loc.T(key), InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => a();
            cm.Items.Add(mi);
            return mi;
        }
        var open = Item("Open", () => { if (Selected().FirstOrDefault() is { } i) _app.Open(i); }, "Enter");
        open.FontWeight = FontWeights.SemiBold;
        Item("OpenFolder", () => { if (Selected().FirstOrDefault() is { } i) _app.OpenFolder(i); });
        cm.Items.Add(new System.Windows.Controls.Separator());
        Item("Resume", () => ForSelected(i => _m.Resume(i)));
        Item("Pause", () => ForSelected(i => _m.Pause(i)));
        Item("Restart", () => ForSelected(i => _m.Restart(i)));
        Item("RefreshUrl", RefreshSelectedUrl);
        Item("CopyUrl", CopyUrl);
        Item("ItemSpeedLimit", SetItemSpeedLimit);
        cm.Items.Add(new System.Windows.Controls.Separator());
        var addQ = Item("AddToQueue", () => { });
        var remQ = Item("RemoveFromQueue", () => { _m.SetQueue(Selected(), null); _view.Refresh(); });
        var extract = Item("Extract", () => { foreach (var i in Selected().Where(IsExtractable)) _ = _app.ExtractAsync(i, automatic: false); });
        var convert = Item("Convert", () => { });
        foreach (var (key, target) in new[] { ("ConvertMp3", PDM.Core.Video.ConvertTarget.Mp3), ("ConvertMp4", PDM.Core.Video.ConvertTarget.Mp4) })
        {
            var mi = new MenuItem { Header = Loc.T(key) };
            mi.Click += (_, _) =>
            {
                var media = Selected().Where(IsConvertible).ToList();
                if (media.Count > 0) new ConvertWindow(_app, media, target).ShowAndActivate();
            };
            convert.Items.Add(mi);
        }
        cm.Items.Add(new System.Windows.Controls.Separator());
        Item("Delete", DeleteSelected, "Del");
        Item("Properties", () => { if (Selected().FirstOrDefault() is { } i) ShowDetails(i); });
        cm.Opened += (_, _) =>
        {
            var sel = Selected();
            foreach (var o in cm.Items.OfType<MenuItem>()) o.IsEnabled = sel.Count > 0;
            // "Add to queue" lists the queues (moving between queues works too).
            addQ.Items.Clear();
            foreach (var q in _app.Settings.Queues)
            {
                var id = q.Id;
                var mi = new MenuItem { Header = Loc.QueueName(q), IsCheckable = true, IsChecked = sel.Count > 0 && sel.All(i => i.QueueId == id) };
                mi.Click += (_, _) => { _m.SetQueue(Selected(), id); _view.Refresh(); };
                addQ.Items.Add(mi);
            }
            addQ.Visibility = sel.Any(i => i.Status != DownloadStatus.Completed) ? Visibility.Visible : Visibility.Collapsed;
            remQ.Visibility = sel.Any(i => i.InQueue) ? Visibility.Visible : Visibility.Collapsed;
            extract.Visibility = sel.Any(IsExtractable) ? Visibility.Visible : Visibility.Collapsed;
            convert.Visibility = sel.Any(IsConvertible) ? Visibility.Visible : Visibility.Collapsed;
        };
        return cm;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---------------- filter / counts ----------------

    private bool Matches(DownloadItem i, string cat) => cat switch
    {
        "All" => true,
        "Active" => i.IsActive || i.Status == DownloadStatus.Queued,
        "Unfinished" => i.Status != DownloadStatus.Completed,
        "Finished" => i.Status == DownloadStatus.Completed,
        _ when cat.StartsWith(QueuePrefix, StringComparison.Ordinal) => i.QueueId == cat[QueuePrefix.Length..],
        _ => Enum.TryParse<FileCategory>(cat, out var fc) && i.Category == fc,
    };

    private bool Filter(object o)
    {
        if (o is not DownloadItem i) return false;
        if (!Matches(i, _category)) return false;
        var q = _search?.Text.Trim();
        return string.IsNullOrEmpty(q) || i.FileName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Url.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshCounts()
    {
        var speed0 = Ui.Speed(_m.TotalSpeed);
        _app.Tray.Update(_m.ActiveCount, speed0);
        _app.UpdatePowerState();
        _app.NoteActivity();
        // Hidden in the tray or minimized: nothing on screen to update, so the list is not walked at all.
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        var items = _m.Items.ToList();
        foreach (var key in _catCounts.Keys)
        {
            var n = items.Count(i => Matches(i, key));
            _catCounts[key].Text = n > 0 ? n.ToString(CultureInfo.CurrentCulture) : "";
        }
        var speed = speed0;
        _statusLeft.Text = Loc.F("StatusBarActive", _m.ActiveCount, string.IsNullOrEmpty(speed) ? "0 B/s" : speed);
        var limit = _app.Settings.SpeedLimitKBps > 0 ? Loc.F("StatusBarLimit", Ui.Size(_app.Settings.SpeedLimitKBps * 1024L)) : Loc.T("NoLimit");
        _browserOk ??= Core.Ipc.NativeHostRegistration.IsRegistered(); // registry + file check: once, not every second
        _statusRight.Text = limit + "   ·   " + (_browserOk == true ? Loc.T("BrowserOk") : Loc.T("BrowserMissing"));
        Title = _m.ActiveCount > 0 ? $"{speed} — {Loc.T("AppTitle")}" : Loc.T("AppTitle");
    }

    private bool? _browserOk;

    /// <summary>The browser bridge was (re)registered: check it again on the next refresh.</summary>
    public void ResetBrowserStatus() => _browserOk = null;

    private void UpdateEmpty() => _empty.Visibility = _view.IsEmpty && _m.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- commands ----------------

    // ---------------- rubber-band selection ----------------
    // Dragging from an empty part of the list draws a frame and selects the rows it touches (like Explorer).
    // Dragging that starts on a row uses the DataGrid's own range selection; Ctrl keeps the earlier selection.

    private readonly Canvas _bandLayer = new() { IsHitTestVisible = false };
    private readonly Border _band = new() { BorderThickness = new Thickness(1), Opacity = 0.6, Visibility = Visibility.Collapsed };
    private Point? _bandStart;
    private bool _banding;
    private List<DownloadItem> _bandKeep = new();

    private void AttachRubberBand(DataGrid g)
    {
        g.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject d) return;
            if (FindParent<DataGridRow>(d) != null || FindParent<DataGridColumnHeader>(d) != null || FindParent<ScrollBar>(d) != null) return;
            _bandStart = e.GetPosition(g);
            _banding = false;
            _bandKeep = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Selected() : new();
            if (_bandKeep.Count == 0) g.UnselectAll();
            g.Focus();
            g.CaptureMouse();
            e.Handled = true;
        };
        g.PreviewMouseMove += (_, e) =>
        {
            if (_bandStart is not { } start) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndBand(g); return; }
            var p = e.GetPosition(g);
            if (!_banding && Math.Abs(p.X - start.X) < 4 && Math.Abs(p.Y - start.Y) < 4) return;
            _banding = true;
            AutoScroll(g, p);
            var rect = new Rect(start, p);
            Canvas.SetLeft(_band, rect.X);
            Canvas.SetTop(_band, rect.Y);
            _band.Width = rect.Width;
            _band.Height = rect.Height;
            _band.Visibility = Visibility.Visible;
            var hits = new List<DownloadItem>(_bandKeep);
            foreach (var item in _view.OfType<DownloadItem>())
            {
                if (g.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row || !row.IsVisible) continue;
                var bounds = row.TransformToAncestor(g).TransformBounds(new Rect(row.RenderSize));
                if (bounds.IntersectsWith(rect) && !hits.Contains(item)) hits.Add(item);
            }
            g.SelectedItems.Clear();
            foreach (var item in hits) g.SelectedItems.Add(item);
            e.Handled = true;
        };
        g.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_bandStart == null) return;
            EndBand(g);
            e.Handled = true;
        };
        g.LostMouseCapture += (_, _) => { if (_bandStart != null) EndBand(g); };
    }

    private void EndBand(DataGrid g)
    {
        _bandStart = null;
        _banding = false;
        _band.Visibility = Visibility.Collapsed;
        if (g.IsMouseCaptured) g.ReleaseMouseCapture();
    }

    /// <summary>Scrolls while the frame is dragged past the top or bottom edge.</summary>
    private static void AutoScroll(DataGrid g, Point p)
    {
        if (FindChild<ScrollViewer>(g) is not { } sv) return;
        if (p.Y < 24) sv.LineUp();
        else if (p.Y > g.ActualHeight - 8) sv.LineDown();
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    private List<DownloadItem> Selected() => _grid.SelectedItems.OfType<DownloadItem>().ToList();

    private void ForSelected(Action<DownloadItem> a)
    {
        foreach (var i in Selected()) a(i);
    }

    private void Activate(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) _app.Open(item);
        else _app.OpenProgress(item);
    }

    /// <summary>Shows one item: "All" category, no search, selected and scrolled into view.</summary>
    private static bool IsConvertible(DownloadItem i) =>
        i.Status == DownloadStatus.Completed && i.Category is FileCategory.Video or FileCategory.Music && System.IO.File.Exists(i.FullPath);

    private static bool IsExtractable(DownloadItem i) =>
        i.Status == DownloadStatus.Completed && PDM.Core.Archives.IsArchive(i.FileName) && System.IO.File.Exists(i.FullPath);

    // ---------------- more: export / import / history ----------------

    private void ShowMoreMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Add(string key, Action a)
        {
            var mi = new MenuItem { Header = Loc.T(key) };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("HistoryStats", () => HistoryWindow.ShowFor(_app, this));
        Add("Grabber", () => new GrabberWindow(_app) { Owner = this }.Show());
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("ExportList", ExportList);
        Add("ImportList", ImportList);
        menu.IsOpen = true;
    }

    /// <summary>Exports what the list shows now (category and search applied).</summary>
    private void ExportList()
    {
        var items = _view.OfType<DownloadItem>().ToList();
        if (items.Count == 0) { Ui.Info(this, Loc.T("ExportEmpty")); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.T("ExportFilter"),
            FileName = "PDM-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            System.IO.File.WriteAllText(dlg.FileName, json ? PDM.Core.ListTransfer.ToJson(items) : PDM.Core.ListTransfer.ToText(items), new System.Text.UTF8Encoding(false));
            _statusLeft.Text = Loc.F("Exported", items.Count);
        }
        catch (Exception ex) { Ui.Info(this, Loc.F("ErrUnknown", ex.Message), "PDM", MessageBoxImage.Error); }
    }

    /// <summary>A .txt with links (one per line or anywhere in the text) or a PDM .json export → the add window.</summary>
    private void ImportList()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("ImportFilter") };
        if (dlg.ShowDialog(this) != true) return;
        List<PDM.Core.ExportEntry> entries;
        try
        {
            var info = new System.IO.FileInfo(dlg.FileName);
            if (info.Length > PDM.Core.ListTransfer.MaxFileBytes) { Ui.Info(this, Loc.T("ImportTooBig")); return; }
            entries = PDM.Core.ListTransfer.Parse(System.IO.File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex) { Ui.Info(this, Loc.F("ErrUnknown", ex.Message), "PDM", MessageBoxImage.Error); return; }
        if (entries.Count == 0) { Ui.Info(this, Loc.T("ImportNone")); return; }
        var requests = entries.Select(e => new AddRequest
        {
            Url = e.Url,
            FileName = e.FileName,
            Referrer = e.Referrer,
            PageUrl = e.PageUrl,
            Kind = e.Kind,
            Video = e.Video,
        }).ToList();
        if (requests.Count == 1) new AddDownloadWindow(_app, requests[0]).ShowAndActivate();
        else new AddDownloadWindow(_app, requests).ShowAndActivate();
    }

    // ---------------- queues ----------------

    /// <summary>The queue chosen in the sidebar, or the main queue.</summary>
    private string SelectedQueueId() =>
        _category.StartsWith(QueuePrefix, StringComparison.Ordinal) ? _category[QueuePrefix.Length..] : QueueInfo.MainId;

    /// <summary>One queue: act at once. Several: a small menu under the button (the sidebar's queue first).</summary>
    private void PickQueue(Button anchor, Action<string> act)
    {
        var queues = _app.Settings.Queues;
        if (queues.Count == 1) { act(queues[0].Id); return; }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var first = SelectedQueueId();
        foreach (var q in queues.OrderBy(q => q.Id == first ? 0 : 1))
        {
            var id = q.Id;
            var mi = new MenuItem { Header = Loc.QueueName(q) };
            mi.Click += (_, _) => act(id);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>Sidebar entries for the queues, below the categories.</summary>
    public void RebuildQueues()
    {
        foreach (var old in _cats.Items.OfType<ListBoxItem>().Where(i => i.Tag is string t && (t.StartsWith(QueuePrefix, StringComparison.Ordinal) || t == "queues-header")).ToList())
        {
            if (old.Tag is string key) _catCounts.Remove(key);
            _cats.Items.Remove(old);
        }
        var header = Ui.Text(Loc.T("Queues"), 11, true, Theme.SubFg);
        header.Margin = new Thickness(10, 10, 0, 6);
        _cats.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false, Tag = "queues-header" });
        foreach (var q in _app.Settings.Queues)
        {
            var key = QueuePrefix + q.Id;
            var g = new Grid { Width = 160 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Ui.Text(Loc.QueueName(q));
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Children.Add(label);
            var count = Ui.Text("", 11.5, brush: Theme.SubFg);
            Grid.SetColumn(count, 1);
            g.Children.Add(count);
            _catCounts[key] = count;
            _cats.Items.Add(new ListBoxItem { Content = g, Tag = key, Padding = new Thickness(8, 5, 8, 5) });
        }
        if (_category.StartsWith(QueuePrefix, StringComparison.Ordinal))
        {
            var current = _cats.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, _category));
            if (current != null) _cats.SelectedItem = current;
            else _cats.SelectedIndex = 1;
        }
        if (IsLoaded) RefreshCounts();
    }

    public void Reveal(DownloadItem item)
    {
        _search.Text = "";
        _cats.SelectedIndex = 1;
        _view.Refresh();
        _grid.SelectedItems.Clear();
        _grid.SelectedItem = item;
        _grid.ScrollIntoView(item);
        _grid.Focus();
    }

    private void SetItemSpeedLimit()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var text = Ui.Prompt(this, Loc.T("ItemSpeedLimit"), Loc.T("ItemSpeedLimitPrompt"), sel[0].SpeedLimitKBps.ToString(CultureInfo.InvariantCulture));
        if (text == null) return;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) || kbps < 0) return;
        foreach (var i in sel) _m.SetSpeedLimit(i, kbps);
    }

    private void ShowDetails(DownloadItem item) => new DetailsWindow(_app, item) { Owner = this }.Show();

    private void AddNew()
    {
        var req = new AddRequest();
        try
        {
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText().Trim();
                if (FileNames.IsHttpUrl(t, out _)) req.Url = t;
            }
        }
        catch { }
        new AddDownloadWindow(_app, req) { Owner = this }.ShowDialog();
    }

    private void PasteLinks()
    {
        if (_search.IsKeyboardFocusWithin) { _search.Paste(); return; }
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        var links = ExtractLinks(text);
        if (links.Count == 0) { AddNew(); return; }
        AddLinks(links);
    }

    private void AddLinks(List<string> links)
    {
        if (links.Count == 1) new AddDownloadWindow(_app, new AddRequest { Url = links[0] }) { Owner = this }.ShowDialog();
        else if (links.Count > 1) new AddDownloadWindow(_app, links.Select(u => new AddRequest { Url = u }).ToList()) { Owner = this }.ShowDialog();
    }

    private static readonly Regex LinkPattern = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<string> ExtractLinks(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2_000_000) return new();
        return LinkPattern.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ';'))
            .Where(u => FileNames.IsHttpUrl(u, out _)).Distinct().Take(BridgeProtocol.MaxItems).ToList();
    }

    private void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var w = new PdmWindow { Title = Loc.T("DeleteTitle"), Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var files = Ui.Check(Loc.T("DeleteFilesToo"), false);
        files.IsEnabled = sel.Any(i => i.Status == DownloadStatus.Completed);
        var sp = new StackPanel { Margin = new Thickness(16), MaxWidth = 460 };
        sp.Children.Add(Ui.Text(Loc.F("DeleteConfirm", sel.Count), wrap: true));
        sp.Children.Add(files);
        sp.Children.Add(Ui.Buttons(Ui.Button(Loc.T("Delete"), (_, _) => w.DialogResult = true, isDefault: true), Ui.Button(Loc.T("Cancel"), isCancel: true)));
        w.Content = sp;
        if (w.ShowDialog() != true) return;
        var deleteFiles = files.IsChecked == true;
        foreach (var i in sel) _m.Remove(i, deleteFiles);
    }

    private void RefreshSelectedUrl()
    {
        if (Selected().FirstOrDefault() is not { } item) return;
        var url = Ui.Prompt(this, Loc.T("RefreshUrl"), Loc.T("RefreshPrompt"), item.Url);
        if (url == null) return;
        if (!FileNames.IsHttpUrl(url.Trim(), out _)) { Ui.Info(this, Loc.T("InvalidUrl")); return; }
        _m.RefreshUrl(item, url.Trim());
    }

    private void CopyUrl()
    {
        var urls = string.Join(Environment.NewLine, Selected().Select(i => i.Url));
        if (urls.Length == 0) return;
        _app.IgnoreClipboardBriefly();
        try { Clipboard.SetText(urls); } catch { }
    }

    private void OpenVideo()
    {
        string? url = null;
        try
        {
            var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (FileNames.IsHttpUrl(t, out _)) url = t;
        }
        catch { }
        new VideoWindow(_app, url ?? "", null, new List<MediaLink>(), null, null) { Owner = this }.Show();
    }

    // ---------------- window ----------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_app.Settings.MinimizeToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _app.Tray.Balloon("PDM", Loc.T("StillRunning"), System.Windows.Forms.ToolTipIcon.None);
            }
        }
        else
        {
            _ = _app.ExitAsync(ask: true);
        }
    }
}

internal sealed class FuncConverter(Func<object?, object?> f) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class FuncMultiConverter(Func<object?[], object> f) : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) => f(values);
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

internal sealed class Cmd(Action a) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => a();
}
