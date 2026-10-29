using System;
using System.Windows;
using WF = System.Windows.Forms;

namespace PDM;

/// <summary>Notification-area icon (WinForms NotifyIcon) with menu and balloons.</summary>
internal sealed class Tray : IDisposable
{
    private readonly PdmApp _app;
    private readonly WF.NotifyIcon _icon;
    private readonly System.Drawing.Icon _idle;
    private readonly System.Drawing.Icon _active;
    private Action? _balloonAction;
    private bool _isActive;
    private string _text = "PDM";

    public Tray(PdmApp app)
    {
        _app = app;
        _idle = LoadIcon("pdm.ico");
        _active = LoadIcon("pdm-active.ico");
        _icon = new WF.NotifyIcon { Icon = _idle, Text = "PDM", Visible = true };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(Loc.T("TrayOpen"), null, (_, _) => _app.ShowMain());
        menu.Items.Add(Loc.T("TrayAdd"), null, (_, _) => new AddDownloadWindow(_app, new Core.Engine.AddRequest()).ShowAndActivate());
        menu.Items.Add(Loc.T("TrayPauseAll"), null, (_, _) => _app.Manager.PauseAll());
        var speed = new WF.ToolStripMenuItem(Loc.T("SpeedLimitToggle"));
        speed.DropDownOpening += (_, _) =>
        {
            // Rebuilt every time it opens, so the current limit is the ticked one.
            speed.DropDownItems.Clear();
            foreach (var (text, isChecked, run) in _app.SpeedLimitChoices(null))
            {
                var action = run;
                var item = new WF.ToolStripMenuItem(text) { Checked = isChecked };
                item.Click += (_, _) => action();
                speed.DropDownItems.Add(item);
            }
        };
        speed.DropDownItems.Add(string.Empty); // gives the item an arrow; replaced when it opens
        menu.Items.Add(speed);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Loc.T("TrayExit"), null, (_, _) => _ = _app.ExitAsync(ask: true));
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) _app.ShowMain(); };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var a = _balloonAction;
            _balloonAction = null;
            a?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;
    }

    private static System.Drawing.Icon LoadIcon(string name)
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + name));
        using var s = info.Stream;
        return new System.Drawing.Icon(s, WF.SystemInformation.SmallIconSize);
    }

    public void Balloon(string title, string text, WF.ToolTipIcon kind, Action? onClick = null)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] : text, kind);
    }

    /// <summary>Called by the main window's timer.</summary>
    public void Update(int active, string speed)
    {
        var isActive = active > 0;
        if (isActive != _isActive)
        {
            _isActive = isActive;
            _icon.Icon = isActive ? _active : _idle;
        }
        var text = isActive ? $"PDM — {active} · {speed}" : "PDM";
        if (text.Length > 63) text = text[..63];
        if (text != _text) { _text = text; _icon.Text = text; } // each set is a call into the shell

    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idle.Dispose();
        _active.Dispose();
    }
}
