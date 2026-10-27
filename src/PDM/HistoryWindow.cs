using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PDM.Core;
using PDM.Core.Engine;
using PDM.Core.Security;

namespace PDM;

/// <summary>Totals (all time, today, 7 and 30 days), a 30-day chart and every finished download, also removed ones.</summary>
internal sealed class HistoryWindow : PdmWindow
{
    private static HistoryWindow? _open;

    private readonly PdmApp _app;
    private readonly ObservableCollection<HistoryEntry> _rows = new();
    private readonly ListCollectionView _view;
    private readonly DataGrid _grid;
    private readonly TextBox _search = new() { Width = 240, Padding = new Thickness(4) };
    private readonly UniformGrid _tiles = new() { Rows = 1, Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Grid _chart = new() { Height = 90 };
    private readonly TextBlock _chartTitle = Ui.Text("", 12, brush: Theme.SubFg);

    public static void ShowFor(PdmApp app, Window owner)
    {
        if (_open != null) { Native.BringToFront(_open); return; }
        _open = new HistoryWindow(app) { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private HistoryWindow(PdmApp app)
    {
        _app = app;
        Title = Loc.T("HistoryStats");
        Width = 920;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o =>
        {
            var q = _search.Text.Trim();
            return q.Length == 0 || o is HistoryEntry e && (e.FileName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || e.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
        };
        _search.TextChanged += (_, _) => _view.Refresh();

        _grid = new DataGrid
        {
            ItemsSource = _view,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            SelectionMode = DataGridSelectionMode.Extended,
            CanUserAddRows = false,
            RowHeight = 22,
        };
        _grid.Columns.Add(Column(Loc.T("FileName"), 260, nameof(HistoryEntry.FileName), null));
        _grid.Columns.Add(Column(Loc.T("FileSize"), 90, nameof(HistoryEntry.Size), v => v is long n && n > 0 ? Ui.Size(n) : ""));
        _grid.Columns.Add(Column(Loc.T("HistoryDate"), 130, nameof(HistoryEntry.CompletedAt), v => v is DateTime t ? t.ToString("g", CultureInfo.CurrentCulture) : ""));
        _grid.Columns.Add(Column(Loc.T("Url"), 0, nameof(HistoryEntry.Url), v => FileNames.Redact(v as string)));
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindRow(d) != null) OpenSelected();
        };

        var root = new DockPanel { Margin = new Thickness(16) };

        var top = new StackPanel();
        top.Children.Add(_tiles);
        top.Children.Add(_chartTitle);
        var chartBorder = new Border { Child = _chart, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 12) };
        chartBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
        top.Children.Add(chartBorder);

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var searchHost = new StackPanel { Orientation = Orientation.Horizontal };
        searchHost.Children.Add(new TextBlock { Text = Loc.T("Search").TrimEnd('…') + ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        searchHost.Children.Add(_search);
        DockPanel.SetDock(searchHost, Dock.Left);
        bar.Children.Add(searchHost);
        var buttons = Ui.Buttons(
            Ui.Button(Loc.T("Open"), (_, _) => OpenSelected()),
            Ui.Button(Loc.T("OpenFolder"), (_, _) => { if (_grid.SelectedItem is HistoryEntry e) PdmApp.SelectInExplorer(e.FullPath); }),
            Ui.Button(Loc.T("DownloadAgain"), (_, _) => DownloadAgain()),
            Ui.Button(Loc.T("Delete"), (_, _) => DeleteSelected()));
        buttons.Margin = new Thickness(0);
        bar.Children.Add(buttons);
        top.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = Ui.Buttons(
            Ui.Button(Loc.T("HistoryClear"), (_, _) => Clear(entries: true, stats: false)),
            Ui.Button(Loc.T("StatsReset"), (_, _) => Clear(entries: false, stats: true)),
            Ui.Button(Loc.T("Close"), (_, _) => Close(), isCancel: true));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Load();
    }

    private static DataGridTextColumn Column(string header, double width, string path, Func<object?, string>? format)
    {
        var binding = new Binding(path);
        if (format != null) binding.Converter = new FuncConverter(format);
        return new DataGridTextColumn
        {
            Header = header,
            Binding = binding,
            Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
        };
    }

    private static DataGridRow? FindRow(DependencyObject d)
    {
        while (d != null && d is not DataGridRow) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as DataGridRow;
    }

    private void Load()
    {
        var data = _app.Manager.History.Snapshot();
        _rows.Clear();
        foreach (var e in data.Entries) _rows.Add(e);

        var today = DateTime.Today;
        (long bytes, int files) Sum(int days)
        {
            long b = 0;
            var f = 0;
            for (var i = 0; i < days; i++)
            {
                if (data.Days.TryGetValue(Key(today.AddDays(-i)), out var d)) { b += d.Bytes; f += d.Files; }
            }
            return (b, f);
        }
        _tiles.Children.Clear();
        _tiles.Children.Add(Tile(Loc.T("StatsTotal"), data.TotalBytes, data.TotalFiles));
        var t = Sum(1);
        _tiles.Children.Add(Tile(Loc.T("StatsToday"), t.bytes, t.files));
        var w = Sum(7);
        _tiles.Children.Add(Tile(Loc.T("Stats7"), w.bytes, w.files));
        var m = Sum(30);
        _tiles.Children.Add(Tile(Loc.T("Stats30"), m.bytes, m.files));

        // Last 30 days, one bar per day (oldest on the left).
        _chartTitle.Text = Loc.T("Stats30Chart");
        _chart.Children.Clear();
        _chart.ColumnDefinitions.Clear();
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).ToList();
        var max = Math.Max(1, days.Max(d => data.Days.TryGetValue(Key(d), out var s) ? s.Bytes : 0));
        for (var i = 0; i < days.Count; i++)
        {
            _chart.ColumnDefinitions.Add(new ColumnDefinition());
            data.Days.TryGetValue(Key(days[i]), out var s);
            var bytes = s?.Bytes ?? 0;
            var barHeight = bytes > 0 ? Math.Max(2, 86.0 * bytes / max) : 0;
            var bar = new Border { Height = barHeight, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(2, 2, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            // A transparent cell over the whole column, so the tooltip also works on empty days.
            var cell = new Grid { Background = System.Windows.Media.Brushes.Transparent, ToolTip = $"{days[i].ToString("d MMM ddd", CultureInfo.CurrentCulture)}: {(bytes > 0 ? Ui.Size(bytes) : "0")} · {Loc.F("FilesCount", s?.Files ?? 0)}" };
            cell.Children.Add(bar);
            Grid.SetColumn(cell, i);
            _chart.Children.Add(cell);
        }
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Border Tile(string label, long bytes, int files)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(label, 12, brush: Theme.SubFg));
        sp.Children.Add(Ui.Text(bytes > 0 ? Ui.Size(bytes) : "0 B", 20, true));
        sp.Children.Add(Ui.Text(Loc.F("FilesCount", files), 12, brush: Theme.SubFg));
        var b = new Border { Child = sp, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        b.SetResourceReference(Border.BackgroundProperty, Theme.Panel);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
        return b;
    }

    private List<HistoryEntry> Selected() => _grid.SelectedItems.OfType<HistoryEntry>().ToList();

    private void OpenSelected()
    {
        if (_grid.SelectedItem is HistoryEntry e) _app.OpenFile(e.FullPath, e.Url);
    }

    private void DownloadAgain()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var requests = sel.Select(e => new AddRequest
        {
            Url = e.Url,
            FileName = e.FileName,
            Referrer = e.Referrer,
            Kind = e.Kind,
            Video = e.Video,
            Folder = e.Folder,
        }).ToList();
        if (requests.Count == 1) new AddDownloadWindow(_app, requests[0]).ShowAndActivate();
        else new AddDownloadWindow(_app, requests).ShowAndActivate();
    }

    private void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        _app.Manager.History.Remove(sel);
        foreach (var e in sel) _rows.Remove(e);
    }

    private void Clear(bool entries, bool stats)
    {
        if (!Ui.Confirm(this, Loc.T(entries ? "HistoryClearConfirm" : "StatsResetConfirm"), "PDM")) return;
        _app.Manager.History.Clear(entries, stats);
        Load();
    }

    private sealed class FuncConverter(Func<object?, string> f) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
