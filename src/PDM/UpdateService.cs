using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PDM.Core;
using PDM.Core.Update;

namespace PDM;

/// <summary>
/// Automatic updates from the project's GitHub releases: checks a few minutes after start and every 6 hours,
/// downloads PDM.exe next to the running exe, checks its SHA-256, and swaps it in when no download is running.
/// A running exe cannot be overwritten but can be renamed, so: PDM.exe → PDM.exe.old, new file → PDM.exe,
/// start the new exe (it waits for this process to exit), exit. The new instance deletes PDM.exe.old.
/// </summary>
internal sealed class UpdateService
{
    private readonly PdmApp _app;
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _toolsTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _toolsBusy;
    private bool _busy;
    private (UpdateInfo Info, string Path)? _pending;

    public UpdateService(PdmApp app)
    {
        _app = app;
        _checkTimer.Tick += async (_, _) =>
        {
            _checkTimer.Interval = TimeSpan.FromHours(6);
            if (_app.Settings.AutoUpdate) await CheckAsync(interactive: false);
        };
        _idleTimer.Tick += (_, _) => TryApplyPending();
        _toolsTimer.Tick += async (_, _) =>
        {
            _toolsTimer.Interval = TimeSpan.FromHours(12);
            await UpdateToolsAsync();
        };
    }

    /// <summary>Keeps yt-dlp current (YouTube changes often). Skipped while a video job is using it.</summary>
    private async Task UpdateToolsAsync()
    {
        var tools = _app.Manager.Tools;
        if (_toolsBusy || !tools.HasYtDlp) return;
        if (_app.Manager.Items.Any(i => i.Kind == PDM.Core.Models.DownloadKind.Video && i.IsActive)) return;
        _toolsBusy = true;
        try
        {
            var v = await Task.Run(() => tools.UpdateYtDlpIfOutdatedAsync(CancellationToken.None));
            if (v != null) Log.Info($"yt-dlp updated to {v}");
        }
        catch (Exception ex)
        {
            Log.Warn("yt-dlp update check failed: " + ex.GetType().Name);
        }
        try
        {
            // Installs added later (Deno) arrive here too; then kept current like yt-dlp.
            var d = await Task.Run(() => tools.UpdateDenoIfNeededAsync(CancellationToken.None));
            if (d != null) Log.Info($"Deno {d} installed");
        }
        catch (Exception ex)
        {
            Log.Warn("Deno update check failed: " + ex.GetType().Name);
        }
        finally
        {
            _toolsBusy = false;
        }
    }

    public static string? CurrentExe => Environment.ProcessPath;
    private static string? StagedPath => CurrentExe is { } e ? Path.Combine(Path.GetDirectoryName(e)!, "PDM.update.exe") : null;

    public void Start()
    {
        // Leftovers of the previous update.
        if (CurrentExe is { } exe)
        {
            try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); } catch { }
            try { if (StagedPath is { } s && File.Exists(s)) File.Delete(s); } catch { }
        }
        _checkTimer.Start();
        _toolsTimer.Start();
    }

    /// <summary>Looks for a newer release; downloads it; installs when idle (or asks, when interactive).</summary>
    public async Task CheckAsync(bool interactive, Window? owner = null, Action<string>? status = null)
    {
        if (_busy) return;
        var exe = CurrentExe;
        if (exe == null || StagedPath is not { } staged) return;
        _busy = true;
        try
        {
            status?.Invoke(Loc.T("UpdChecking"));
            var info = await Task.Run(() => UpdateClient.CheckAsync(AppPaths.Version, CancellationToken.None));
            if (info == null)
            {
                status?.Invoke(Loc.F("UpdUpToDate", AppPaths.Version));
                return;
            }
            Log.Info($"Update {info.Version} found");
            var progress = new Progress<double>(p => status?.Invoke(Loc.F("UpdDownloading", info.Version, p)));
            await Task.Run(() => UpdateClient.DownloadAsync(info, staged, progress, CancellationToken.None));
            _pending = (info, staged);
            status?.Invoke(Loc.F("UpdReady", info.Version));
            if (interactive)
            {
                if (Ui.Confirm(owner, Loc.F("UpdAskRestart", info.Version), "PDM")) await ApplyAsync();
                else _idleTimer.Start();
            }
            else
            {
                TryApplyPending();
                if (_pending != null) _idleTimer.Start();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            Log.Warn("Update check: GitHub rate limit");
            status?.Invoke(Loc.T("UpdBusy"));
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            status?.Invoke(Loc.F("UpdFailed", ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Installs the staged update only while nothing is downloading.</summary>
    private void TryApplyPending()
    {
        if (_pending == null || _app.Manager.ActiveCount > 0) return;
        _idleTimer.Stop();
        _ = ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        if (_pending is not { } p || CurrentExe is not { } exe) return;
        var old = exe + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(p.Path, exe); }
            catch
            {
                File.Move(old, exe); // put the working version back
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Installing update failed", ex);
            _app.Tray.Balloon("PDM", Loc.F("UpdFailed", ex.Message), System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }
        _pending = null;
        Log.Info($"Updated to {p.Info.Version}; restarting");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--updated");
            if (!_app.Main.IsVisible) psi.ArgumentList.Add("--tray");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("Restart after update failed", ex);
        }
        await _app.ExitAsync(ask: false);
    }
}
