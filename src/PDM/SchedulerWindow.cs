using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PDM.Core;
using PDM.Core.Models;

namespace PDM;

/// <summary>Queues and their schedules (like IDM's "Scheduler"): several named queues, each with start/stop times,
/// days, how many files at once and what to do when it finishes.</summary>
internal sealed class SchedulerWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly List<QueueInfo> _queues;
    private readonly HashSet<string> _removed = new();
    private readonly ListBox _list = new() { Width = 180, Height = 300 };
    private readonly CheckBox _enable;
    private readonly CheckBox _stopEnable;
    private readonly TextBox _start = new() { Width = 80, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _stop = new() { Width = 80, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _after = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _parallel = new() { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly Button _rename;
    private readonly Button _delete;
    private QueueInfo? _current;

    // Monday first, as on Turkish calendars; values are DayOfWeek numbers.
    private static readonly int[] DayOrder = [1, 2, 3, 4, 5, 6, 0];

    public SchedulerWindow(PdmApp app, string? selectQueue = null)
    {
        _app = app;
        _queues = app.Settings.Queues.Select(Clone).ToList();
        Title = Loc.T("SchedTitle");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _enable = Ui.Check(Loc.T("SchedEnable"), false);
        _stopEnable = Ui.Check(Loc.T("SchedStopEnable"), false);
        foreach (var (key, action) in new[] { ("PostNone", PostQueueAction.None), ("PostExit", PostQueueAction.ExitApp), ("PostSleep", PostQueueAction.Sleep), ("PostShutdown", PostQueueAction.Shutdown) })
            _after.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = action });
        _parallel.Items.Add(new ComboBoxItem { Content = Loc.T("QueueParallelDefault"), Tag = 0 });
        for (var i = 1; i <= 10; i++) _parallel.Items.Add(new ComboBoxItem { Content = i.ToString(CultureInfo.InvariantCulture), Tag = i });
        var dayNames = Loc.T("DayNames").Split(',');
        var days = new WrapPanel();
        for (var i = 0; i < 7; i++)
        {
            _days[i] = new CheckBox { Content = dayNames[i], Margin = new Thickness(0, 2, 10, 2), Tag = DayOrder[i] };
            days.Children.Add(_days[i]);
        }

        // Left: queue list with New / Rename / Delete.
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Ui.Text(Loc.T("Queues"), 12, true, Theme.SubFg));
        _list.Margin = new Thickness(0, 4, 0, 6);
        left.Children.Add(_list);
        _rename = Ui.Button(Loc.T("QueueRename"), (_, _) => Rename());
        _delete = Ui.Button(Loc.T("Delete"), (_, _) => Delete());
        var newBtn = Ui.Button(Loc.T("QueueNew"), (_, _) => AddQueue());
        foreach (var b in new[] { newBtn, _rename, _delete }) { b.Margin = new Thickness(0, 0, 0, 4); b.HorizontalAlignment = HorizontalAlignment.Stretch; left.Children.Add(b); }

        // Right: the selected queue's schedule.
        var right = new StackPanel();
        right.Children.Add(_enable);
        right.Children.Add(Ui.Field(Loc.T("SchedStart") + " (HH:mm)", _start, 160));
        right.Children.Add(_stopEnable);
        right.Children.Add(Ui.Field(Loc.T("SchedStop") + " (HH:mm)", _stop, 160));
        right.Children.Add(Ui.Text(Loc.T("SchedDays"), 12, brush: Theme.SubFg));
        right.Children.Add(days);
        right.Children.Add(Ui.Field(Loc.T("QueueParallel"), _parallel, 160));
        right.Children.Add(Ui.Field(Loc.T("SchedAfter"), _after, 160));
        right.Children.Add(Ui.Text(Loc.T("SchedHint"), 12, brush: Theme.SubFg, wrap: true));
        var startNow = Ui.Button(Loc.T("StartQueue"), (_, _) => { if (_current != null) _app.Manager.StartQueue(_current.Id); });
        var stopNow = Ui.Button(Loc.T("StopQueue"), (_, _) => { if (_current != null) _app.Manager.StopQueue(_current.Id); });
        var nowRow = Ui.Buttons(startNow, stopNow);
        nowRow.HorizontalAlignment = HorizontalAlignment.Left;
        right.Children.Add(nowRow);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(right);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(body);
        root.Children.Add(Ui.Buttons(Ui.Button(Loc.T("Save"), (_, _) => Save(), isDefault: true), Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isCancel: true)));
        Content = root;

        FillList(selectQueue ?? QueueInfo.MainId);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is not ListBoxItem { Tag: QueueInfo q } || q == _current) return;
            if (!Commit()) { SelectQueue(_current!); return; }
            Show(q);
        };
    }

    private static QueueInfo Clone(QueueInfo q) => new()
    {
        Id = q.Id, Name = q.Name, ScheduleEnabled = q.ScheduleEnabled, StartAt = q.StartAt, StopAt = q.StopAt,
        Days = q.Days.ToList(), MaxConcurrent = q.MaxConcurrent, PostAction = q.PostAction,
    };

    private void FillList(string selectId)
    {
        _list.Items.Clear();
        foreach (var q in _queues) _list.Items.Add(new ListBoxItem { Content = Loc.QueueName(q), Tag = q, Padding = new Thickness(6, 4, 6, 4) });
        var sel = _queues.FirstOrDefault(q => q.Id == selectId) ?? _queues[0];
        _current = null;
        Show(sel);
        SelectQueue(sel);
    }

    private void SelectQueue(QueueInfo q)
    {
        foreach (ListBoxItem item in _list.Items)
            if (item.Tag == q) { _list.SelectedItem = item; break; }
    }

    private void Show(QueueInfo q)
    {
        _current = q;
        _enable.IsChecked = q.ScheduleEnabled && q.StartAt != null;
        _stopEnable.IsChecked = q.ScheduleEnabled && q.StopAt != null;
        _start.Text = (q.StartAt ?? DateTime.Today.AddHours(2)).ToString("HH:mm", CultureInfo.InvariantCulture);
        _stop.Text = (q.StopAt ?? DateTime.Today.AddHours(8)).ToString("HH:mm", CultureInfo.InvariantCulture);
        foreach (var cb in _days) cb.IsChecked = q.Days.Count == 0 || q.Days.Contains((int)cb.Tag);
        _after.SelectedItem = _after.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (PostQueueAction)i.Tag == q.PostAction) ?? _after.Items[0];
        _parallel.SelectedItem = _parallel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == q.MaxConcurrent) ?? _parallel.Items[0];
        var isMain = q.Id == QueueInfo.MainId;
        _rename.IsEnabled = !isMain;
        _delete.IsEnabled = !isMain;
    }

    /// <summary>Next occurrence of HH:mm (today if still ahead, otherwise tomorrow).</summary>
    private static DateTime? Next(string text)
    {
        if (!TimeSpan.TryParseExact(text.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) || t >= TimeSpan.FromDays(1)) return null;
        var at = DateTime.Today + t;
        return at <= DateTime.Now ? at.AddDays(1) : at;
    }

    /// <summary>Copies the editor into the shown queue; false (and focus) when a time is invalid.</summary>
    private bool Commit()
    {
        var q = _current;
        if (q == null) return true;
        DateTime? start = null, stop = null;
        if (_enable.IsChecked == true && (start = Next(_start.Text)) == null) { _start.Focus(); return false; }
        if (_stopEnable.IsChecked == true && (stop = Next(_stop.Text)) == null) { _stop.Focus(); return false; }
        q.StartAt = start;
        q.StopAt = stop;
        q.ScheduleEnabled = start != null || stop != null;
        q.Days = _days.Where(d => d.IsChecked == true).Select(d => (int)d.Tag).ToList();
        if (q.Days.Count == 0 && q.ScheduleEnabled) { _days[0].Focus(); return false; }
        if (q.Days.Count == 7) q.Days.Clear();
        q.PostAction = _after.SelectedItem is ComboBoxItem { Tag: PostQueueAction a } ? a : PostQueueAction.None;
        q.MaxConcurrent = _parallel.SelectedItem is ComboBoxItem { Tag: int n } ? n : 0;
        return true;
    }

    private void AddQueue()
    {
        if (!Commit()) return;
        if (_queues.Count >= 20) return;
        var name = Ui.Prompt(this, Loc.T("QueueNew"), Loc.T("QueueNamePrompt"), Loc.F("QueueDefaultName", _queues.Count + 1))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var q = new QueueInfo { Id = "q" + Guid.NewGuid().ToString("N")[..8], Name = name.Length > 40 ? name[..40] : name };
        _queues.Add(q);
        FillList(q.Id);
    }

    private void Rename()
    {
        if (_current is not { } q || q.Id == QueueInfo.MainId || !Commit()) return;
        var name = Ui.Prompt(this, Loc.T("QueueRename"), Loc.T("QueueNamePrompt"), q.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        q.Name = name.Length > 40 ? name[..40] : name;
        FillList(q.Id);
    }

    private void Delete()
    {
        if (_current is not { } q || q.Id == QueueInfo.MainId) return;
        if (!Ui.Confirm(this, Loc.F("QueueDeleteConfirm", Loc.QueueName(q)), "PDM")) return;
        _queues.Remove(q);
        _removed.Add(q.Id);
        _current = null;
        FillList(QueueInfo.MainId);
    }

    private void Save()
    {
        if (!Commit()) return;
        var s = _app.Settings;
        s.Queues = _queues;
        foreach (var id in _removed) _app.Manager.QueueRemoved(id);
        _app.SaveSettings();
        _app.Main.RebuildQueues();
        Close();
    }
}

/// <summary>Cancellable countdown before sleep/shutdown when the queue finishes.</summary>
internal sealed class CountdownWindow : PdmWindow
{
    public CountdownWindow(PdmApp app, PostQueueAction action)
    {
        Title = "PDM";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var actionText = Loc.T(action == PostQueueAction.Sleep ? "PostSleep" : "PostShutdown");
        var remaining = 60;
        var text = Ui.Text(Loc.F("ShutdownCountdown", remaining, actionText), 14, wrap: true);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(text);
        root.Children.Add(Ui.Buttons(
            Ui.Button(Loc.T("Ok"), (_, _) => { timer.Stop(); Close(); app.RunPostAction(action); }),
            Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isDefault: true, isCancel: true)));
        Content = root;
        timer.Tick += (_, _) =>
        {
            // New work started during the countdown (a browser download, another queue): sleeping or shutting down now
            // would cut it off. The countdown is cancelled; the user can still choose the action again later.
            if (app.Manager.ActiveCount > 0)
            {
                timer.Stop();
                Log.Info("Post-queue action cancelled: a download started during the countdown");
                Close();
                return;
            }
            remaining--;
            text.Text = Loc.F("ShutdownCountdown", remaining, actionText);
            if (remaining > 0) return;
            timer.Stop();
            Close();
            app.RunPostAction(action);
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }
}
