using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PDM.Core;
using PDM.Core.Models;
using PDM.Core.Video;

namespace PDM;

/// <summary>Converts finished files to MP3 or MP4 one after another, with progress; installs FFmpeg first if asked.</summary>
internal sealed class ConvertWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly List<DownloadItem> _items;
    private readonly ConvertTarget _target;
    private readonly TextBlock _title = Ui.Text("", 14, true);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 16, Maximum = 100, Margin = new Thickness(0, 10, 0, 6) };
    private readonly Button _action;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;

    public ConvertWindow(PdmApp app, List<DownloadItem> items, ConvertTarget target)
    {
        _app = app;
        _items = items;
        _target = target;
        Title = Loc.T(target == ConvertTarget.Mp3 ? "ConvertMp3" : "ConvertMp4");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(_title);
        root.Children.Add(_progress);
        root.Children.Add(_status);
        _action = Ui.Button(Loc.T("Install"), (_, _) => _ = InstallThenRunAsync());
        _action.Visibility = Visibility.Collapsed;
        root.Children.Add(Ui.Buttons(_action, Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isCancel: true)));
        Content = root;
        Closed += (_, _) => _cts.Cancel();
        Loaded += (_, _) =>
        {
            if (_app.Manager.Tools.HasFfmpeg) _ = RunAsync();
            else
            {
                _title.Text = Loc.T("FfmpegNeededTitle");
                _status.Text = Loc.T("FfmpegNeeded");
                _progress.Visibility = Visibility.Collapsed;
                _action.Visibility = Visibility.Visible;
            }
        };
    }

    private async Task InstallThenRunAsync()
    {
        if (_running) return;
        _running = true;
        _action.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Visible;
        try
        {
            await VideoWindow.InstallToolsAsync(_app.Manager.Tools, p => Dispatcher.BeginInvoke(() => { _progress.Value = p; _status.Text = Loc.F("Installing", p); }), _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _status.Text = Loc.F("ErrUnknown", ex.Message);
            _running = false;
            return;
        }
        _running = false;
        await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        var converter = new MediaConverter(_app.Manager.Tools);
        var failed = new List<string>();
        DownloadItem? last = null;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            _title.Text = (_items.Count > 1 ? $"{i + 1}/{_items.Count}  " : "") + item.FileName;
            _progress.Value = 0;
            _status.Text = Loc.T("Converting");
            converter.Progress = p => Dispatcher.BeginInvoke(() =>
            {
                _progress.Value = p * 100;
                _status.Text = Loc.F("ConvertingPct", p * 100);
            });
            try
            {
                var path = await Task.Run(() => converter.ConvertAsync(item.FullPath, _target, _cts.Token));
                last = _app.Manager.AddFinishedFile(path, item);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn($"Convert failed: {ex.GetType().Name}: {ex.Message}");
                failed.Add(item.FileName + ": " + Reason(ex));
            }
        }
        _running = false;
        if (failed.Count == 0 && last != null)
        {
            Close();
            new CompleteWindow(_app, last).ShowAndActivate();
            return;
        }
        _progress.Visibility = Visibility.Collapsed;
        _title.Text = Loc.T("ConvertFailed");
        _status.Text = string.Join("\n", failed);
    }

    private static string Reason(Exception ex) => ex switch
    {
        NotSupportedException { Message: "no-audio" } => Loc.T("ConvertNoAudio"),
        NotSupportedException { Message: "no-video" } => Loc.T("ConvertNoVideo"),
        FileNotFoundException => Loc.T("ConvertMissing"),
        _ when DiskSpace.IsDiskFull(ex) => Loc.ErrorText("diskfull"),
        _ => ex.Message,
    };
}
