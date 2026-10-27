using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PDM.Core;
using PDM.Core.Engine;
using PDM.Core.Models;
using PDM.Core.Security;

namespace PDM;

/// <summary>New download dialog (single link with live check) or batch mode (checklist with filter).</summary>
internal sealed class AddDownloadWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly AddRequest _template;
    private readonly List<AddRequest>? _batch;
    private readonly TextBox _url = new() { Padding = new Thickness(4) };
    private readonly TextBox _name = new() { Padding = new Thickness(4) };
    private readonly TextBox _folder = new() { Padding = new Thickness(4) };
    private readonly ComboBox _connections = new() { Width = 70, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _sha = new() { Padding = new Thickness(4), FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly TextBox _referrer = new() { Padding = new Thickness(4) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _warn = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _loginButton = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly ListBox _list = new() { Height = 260 };
    private readonly TextBox _filter = new() { Padding = new Thickness(4) };
    private readonly ComboBox _queue = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _checkStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Dictionary<CheckBox, string> _rowText = new();
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _probeCts;
    private bool _nameEdited;
    private bool _settingName;
    private string? _probedName;
    /// <summary>The folder shows the category folder for the current name (Downloads\Compressed …) until the user picks one.</summary>
    private bool _autoFolder;
    private bool _settingFolder;
    private long _probedSize = -1;

    /// <summary>Single download (URL may be empty).</summary>
    public AddDownloadWindow(PdmApp app, AddRequest request) : this(app, request, null) { }

    /// <summary>Batch: several links at once.</summary>
    public AddDownloadWindow(PdmApp app, List<AddRequest> batch) : this(app, batch.FirstOrDefault() ?? new AddRequest(), batch) { }

    private AddDownloadWindow(PdmApp app, AddRequest template, List<AddRequest>? batch)
    {
        _app = app;
        _template = template;
        _batch = batch is { Count: > 1 } ? batch : null;
        Title = _batch != null ? Loc.F("BatchLinks", _batch.Count) : (template.Cookies != null || template.PageUrl != null ? Loc.T("BrowserDownloadTitle") : Loc.T("AddTitle"));
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        for (var i = 1; i <= 16; i++) _connections.Items.Add(i);
        _connections.SelectedItem = Math.Clamp(template.Connections ?? app.Settings.DefaultConnections, 1, 16);
        _folder.Text = string.IsNullOrWhiteSpace(template.Folder) ? app.Settings.DownloadFolder : template.Folder;
        _autoFolder = string.IsNullOrWhiteSpace(template.Folder) || string.Equals(template.Folder, app.Settings.DownloadFolder, StringComparison.OrdinalIgnoreCase);
        _folder.TextChanged += (_, _) => { if (!_settingFolder) _autoFolder = false; };
        _warn.SetResourceReference(TextBlock.ForegroundProperty, Theme.Bad);
        _info.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);

        var root = new StackPanel { Margin = new Thickness(16) };
        if (_batch == null) BuildSingle(root); else BuildBatch(root);

        var browse = Ui.Button(Loc.T("Browse"), (_, _) => PickFolder());
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        root.Children.Add(Ui.Field(Loc.T("SaveTo"), folderRow, 120));
        if (_batch != null && app.Settings.UseCategoryFolders)
        {
            var hint = Ui.Text(Loc.T("CategoryFolderHint"), 12, brush: Theme.SubFg, wrap: true);
            hint.Margin = new Thickness(120, 0, 0, 4);
            root.Children.Add(hint);
        }
        root.Children.Add(Ui.Field(Loc.T("Connections"), _connections, 120));
        // Several queues: choose where "Download later" puts it.
        foreach (var q in app.Settings.Queues) _queue.Items.Add(new ComboBoxItem { Content = Loc.QueueName(q), Tag = q.Id });
        _queue.SelectedIndex = 0;
        if (app.Settings.Queues.Count > 1) root.Children.Add(Ui.Field(Loc.T("QueueForLater"), _queue, 120));

        if (_batch == null)
        {
            var adv = new StackPanel();
            adv.Children.Add(Ui.Field(Loc.T("ExpectedSha"), _sha, 200));
            adv.Children.Add(Ui.Field(Loc.T("Referrer"), _referrer, 200));
            _referrer.Text = template.Referrer ?? "";
            _sha.Text = template.ExpectedSha256 ?? "";
            root.Children.Add(new Expander { Header = Loc.T("Advanced"), Content = adv, Margin = new Thickness(0, 6, 0, 0) });
            root.Children.Add(_info);
            root.Children.Add(_loginButton);
            root.Children.Add(_warn);
            _loginButton.Content = Loc.T("LoginAdd");
            _loginButton.Click += (_, _) => AddLogin();
        }

        root.Children.Add(Ui.Buttons(
            Ui.Button(Loc.T("DownloadNow"), (_, _) => Submit(true), isDefault: true),
            Ui.Button(Loc.T("DownloadLater"), (_, _) => Submit(false)),
            Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isCancel: true)));
        Content = root;

        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = ProbeAsync(); };
        Loaded += (_, _) =>
        {
            if (_batch != null) return;
            if (string.IsNullOrEmpty(_url.Text)) _url.Focus();
            else { _ = ProbeAsync(); _name.Focus(); _name.SelectAll(); }
        };
        Closed += (_, _) => { _probeCts?.Cancel(); _checkCts?.Cancel(); _debounce.Stop(); };
    }

    private void BuildSingle(StackPanel root)
    {
        _url.Text = _template.Url;
        _settingName = true;
        _name.Text = _template.FileName ?? FileNames.Sanitize(FileNames.FromUrl(_template.Url));
        _settingName = false;
        if (string.IsNullOrEmpty(_template.Url)) _name.Text = "";
        _nameEdited = !string.IsNullOrWhiteSpace(_template.FileName);
        _url.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
            if (!_nameEdited)
            {
                _settingName = true;
                _name.Text = FileNames.IsHttpUrl(_url.Text.Trim(), out _) ? FileNames.Sanitize(FileNames.FromUrl(_url.Text.Trim())) : "";
                _settingName = false;
            }
        };
        _name.TextChanged += (_, _) =>
        {
            if (!_settingName) _nameEdited = true;
            UpdateWarnings();
        };
        _folder.TextChanged += (_, _) => UpdateWarnings();
        root.Children.Add(Ui.Field(Loc.T("Url"), _url, 120));
        root.Children.Add(Ui.Field(Loc.T("FileName"), _name, 120));
        if (_template.SizeHint > 0) _info.Text = Ui.Size(_template.SizeHint);
        UpdateWarnings();
    }

    private void BuildBatch(StackPanel root)
    {
        foreach (var r in _batch!)
        {
            var name = r.FileName ?? FileNames.Sanitize(FileNames.FromUrl(r.Url));
            var cb = new CheckBox
            {
                IsChecked = true,
                Tag = r,
                Content = new TextBlock { Text = name + "   " + FileNames.Redact(r.Url), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 },
                ToolTip = FileNames.Redact(r.Url),
                Margin = new Thickness(2),
            };
            if (_app.Manager.FindDuplicate(r.Url) is { } dup)
            {
                // Already in the list: left unticked, marked.
                cb.IsChecked = false;
                ((TextBlock)cb.Content).Text = Loc.F("AlreadyInList", Loc.StatusText(dup.Status)) + "   " + ((TextBlock)cb.Content).Text;
            }
            _rowText[cb] = ((TextBlock)cb.Content).Text;
            _list.Items.Add(cb);
        }
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var all = Ui.Button(Loc.T("SelectAll"), (_, _) => SetAll(true));
        var none = Ui.Button(Loc.T("SelectNone"), (_, _) => SetAll(false));
        var check = Ui.Button(Loc.T("CheckLinks"), (_, _) => _ = CheckLinksAsync());
        check.ToolTip = Loc.T("CheckLinksTip");
        DockPanel.SetDock(none, Dock.Right);
        DockPanel.SetDock(all, Dock.Right);
        DockPanel.SetDock(check, Dock.Right);
        bar.Children.Add(none);
        bar.Children.Add(all);
        bar.Children.Add(check);
        _filter.ToolTip = Loc.T("Filter");
        _filter.TextChanged += (_, _) => ApplyFilter();
        bar.Children.Add(_filter);
        root.Children.Add(Ui.Text(Loc.T("Filter"), brush: Theme.SubFg));
        root.Children.Add(bar);
        root.Children.Add(_list);
        _checkStatus.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);
        root.Children.Add(_checkStatus);
    }

    /// <summary>Asks each visible link for its size, four at a time; links that fail are unticked and marked.</summary>
    private async System.Threading.Tasks.Task CheckLinksAsync()
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        var boxes = Boxes.Where(b => b.Visibility == Visibility.Visible && ((AddRequest)b.Tag).Kind == DownloadKind.Http).ToList();
        if (boxes.Count == 0) return;
        int done = 0, bad = 0;
        long total = 0;
        _checkStatus.Text = Loc.F("CheckingLinks", 0, boxes.Count);
        using var gate = new SemaphoreSlim(4);
        async System.Threading.Tasks.Task CheckOne(CheckBox cb)
        {
            await gate.WaitAsync(cts.Token);
            try
            {
                var r = (AddRequest)cb.Tag;
                string label;
                var ok = false;
                using var one = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                one.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    if (!FileNames.IsHttpUrl(r.Url, out var uri)) throw new ArgumentException("url");
                    var job = new DownloadJob
                    {
                        Url = uri!,
                        Referrer = r.Referrer,
                        UserAgent = !string.IsNullOrWhiteSpace(_app.Settings.CustomUserAgent) ? _app.Settings.CustomUserAgent : r.UserAgent ?? EngineOptions.DefaultUserAgent,
                        Cookies = _app.Settings.AcceptBrowserCookies ? Cookies.Build(r.Cookies) : null,
                    };
                    var info = await System.Threading.Tasks.Task.Run(() => HttpDownloader.ProbeAsync(job, one.Token), one.Token);
                    ok = true;
                    if (info.TotalBytes > 0) { r.SizeHint = info.TotalBytes; total += info.TotalBytes; }
                    label = info.TotalBytes > 0 ? Ui.Size(info.TotalBytes) : Loc.T("SizeUnknown");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    label = "✗ " + (ex is HttpRequestException h && h.StatusCode is { } sc ? ((int)sc).ToString(CultureInfo.InvariantCulture)
                        : ex is OperationCanceledException ? Loc.T("ProbeTimeout") : Loc.T("Unreachable"));
                }
                if (cts.IsCancellationRequested) return;
                ((TextBlock)cb.Content).Text = "[" + label + "]   " + _rowText[cb];
                if (!ok)
                {
                    cb.IsChecked = false;
                    cb.SetResourceReference(ForegroundProperty, Theme.Bad);
                    bad++;
                }
                done++;
                _checkStatus.Text = Loc.F("CheckingLinks", done, boxes.Count);
            }
            finally { gate.Release(); }
        }
        try { await System.Threading.Tasks.Task.WhenAll(boxes.Select(CheckOne)); }
        catch (OperationCanceledException) { return; }
        _checkStatus.Text = Loc.F("LinksChecked", boxes.Count - bad, bad, total > 0 ? Ui.Size(total) : "?");
    }

    private IEnumerable<CheckBox> Boxes => _list.Items.OfType<CheckBox>();

    private void SetAll(bool on)
    {
        foreach (var cb in Boxes.Where(b => b.Visibility == Visibility.Visible)) cb.IsChecked = on;
    }

    /// <summary>Filter: space-separated extensions (".zip .pdf") and/or words; non-matching links are hidden and unchecked.</summary>
    private void ApplyFilter()
    {
        var terms = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var cb in Boxes)
        {
            var r = (AddRequest)cb.Tag;
            var name = r.FileName ?? FileNames.FromUrl(r.Url) ?? "";
            var ok = terms.Length == 0 || terms.Any(t => t.StartsWith('.')
                ? name.EndsWith(t, StringComparison.OrdinalIgnoreCase)
                : r.Url.Contains(t, StringComparison.OrdinalIgnoreCase) || name.Contains(t, StringComparison.CurrentCultureIgnoreCase));
            cb.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            cb.IsChecked = ok;
        }
    }

    /// <summary>The address is already in the list: show it, or download again. Returns true for "download again".</summary>
    private bool AskDuplicate(DownloadItem dup)
    {
        var w = new PdmWindow { Title = "PDM", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var again = false;
        var sp = new StackPanel { Margin = new Thickness(16), MaxWidth = 520 };
        sp.Children.Add(Ui.Text(Loc.F("DuplicateText", dup.FileName, Loc.StatusText(dup.Status)), wrap: true));
        var show = Ui.Button(dup.Status == DownloadStatus.Completed && File.Exists(dup.FullPath) ? Loc.T("Open") : Loc.T("DuplicateShow"), (_, _) =>
        {
            w.Close();
            if (dup.Status == DownloadStatus.Completed && File.Exists(dup.FullPath)) _app.Open(dup);
            else { _app.ShowMain(); _app.Main.Reveal(dup); }
            Close();
        }, isDefault: true);
        sp.Children.Add(Ui.Buttons(show, Ui.Button(Loc.T("DuplicateAgain"), (_, _) => { again = true; w.Close(); }), Ui.Button(Loc.T("Cancel"), (_, _) => w.Close(), isCancel: true)));
        w.Content = sp;
        w.ShowDialog();
        return again;
    }

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : _app.Settings.DownloadFolder };
        if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
    }

    private void UpdateWarnings()
    {
        var name = _name.Text.Trim();
        if (_autoFolder && _batch == null)
        {
            // Show where the file will really go; the final choice is made with the server's name.
            var s = _app.Settings;
            var sub = s.UseCategoryFolders && name.Length > 0 ? FileCategories.FolderName(FileCategories.Of(name)) : "";
            _settingFolder = true;
            _folder.Text = sub.Length > 0 ? Path.Combine(s.DownloadFolder, sub) : s.DownloadFolder;
            _settingFolder = false;
        }
        var msgs = new List<string>();
        if (FileCategories.IsExecutable(name)) msgs.Add(Loc.T("ExeWarning"));
        try
        {
            var folder = _folder.Text.Trim();
            if (name.Length > 0 && Directory.Exists(folder))
            {
                var clean = FileNames.Sanitize(name);
                if (File.Exists(Path.Combine(folder, clean))) msgs.Add(Loc.F("FileExists", FileNames.MakeUnique(folder, clean)));
            }
            if (_probedSize > 0 && folder.Length > 0 && !DiskSpace.HasRoomFor(folder, _probedSize))
                msgs.Add(Loc.F("NoSpace", Ui.Size(DiskSpace.FreeBytes(folder)), Ui.Size(_probedSize)));
        }
        catch { }
        _warn.Text = string.Join("\n", msgs);
        _warn.Visibility = msgs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async System.Threading.Tasks.Task ProbeAsync()
    {
        _probeCts?.Cancel();
        var text = _url.Text.Trim();
        _probedSize = -1;
        if (BatchPattern.HasPattern(text))
        {
            _info.Text = Loc.F("PatternFound", BatchPattern.Expand(text).Count);
            return;
        }
        if (!FileNames.IsHttpUrl(text, out var uri))
        {
            _info.Text = text.Length > 0 ? Loc.T("InvalidUrl") : "";
            return;
        }
        var cts = _probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _info.Text = Loc.T("Checking");
        _loginButton.Visibility = Visibility.Collapsed;
        try
        {
            var job = new DownloadJob
            {
                Url = uri!,
                Referrer = string.IsNullOrWhiteSpace(_referrer.Text) ? _template.Referrer : _referrer.Text.Trim(),
                UserAgent = !string.IsNullOrWhiteSpace(_app.Settings.CustomUserAgent) ? _app.Settings.CustomUserAgent : _template.UserAgent ?? EngineOptions.DefaultUserAgent,
                Cookies = _app.Settings.AcceptBrowserCookies ? Cookies.Build(_template.Cookies) : null,
            };
            var info = await System.Threading.Tasks.Task.Run(() => HttpDownloader.ProbeAsync(job, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !IsLoaded) return;
            var parts = new List<string> { info.TotalBytes > 0 ? Ui.Size(info.TotalBytes) : Loc.T("SizeUnknown") };
            parts.Add(info.SupportsRanges && info.StatusCode == 206 ? Loc.T("Resumable") : Loc.T("NotResumable"));
            if (!string.IsNullOrEmpty(info.ContentType)) parts.Add(info.ContentType!);
            _info.Text = string.Join("  ·  ", parts);
            _probedSize = info.TotalBytes;
            UpdateWarnings();
            var serverName = info.FileName ?? FileNames.FromUrl(info.FinalUrl.AbsoluteUri);
            if (!_nameEdited && !string.IsNullOrWhiteSpace(serverName))
            {
                _probedName = FileNames.EnsureExtension(FileNames.Sanitize(serverName), info.ContentType);
                _settingName = true;
                _name.Text = _probedName;
                _settingName = false;
                UpdateWarnings();
            }
        }
        catch (OperationCanceledException) when (cts != _probeCts) { }
        catch (Exception ex)
        {
            if (cts != _probeCts) return;
            var reason = ex is HttpRequestException h && h.StatusCode is { } sc ? Loc.ErrorText("http:" + ((int)sc).ToString(CultureInfo.InvariantCulture))
                : ex is OperationCanceledException ? "timeout" : ex.GetType().Name;
            _info.Text = Loc.F("ProbeFailed", reason);
            if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                _info.Text = Loc.T("LoginNeeded");
                _loginButton.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>The site answered 401: save a login for it and check again.</summary>
    private void AddLogin()
    {
        if (!FileNames.IsHttpUrl(_url.Text.Trim(), out var uri)) return;
        var host = uri!.Host.ToLowerInvariant();
        var existing = NetConfig.FindLogin(host);
        var login = LoginDialog.Edit(this, existing, host);
        if (login == null) return;
        _app.Settings.Logins.RemoveAll(l => l.Host == login.Host);
        _app.Settings.Logins.Add(login);
        _app.SaveSettings();
        _ = ProbeAsync();
    }

    private string SelectedQueue() => _queue.SelectedItem is ComboBoxItem { Tag: string id } ? id : QueueInfo.MainId;

    private void Submit(bool startNow)
    {
        // Untouched folder: the manager picks the category folder from the final (server) name.
        var folder = _autoFolder ? _app.Settings.DownloadFolder : _folder.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) { Ui.Info(this, Loc.T("SaveTo") + ": ?"); return; }
        var connections = _connections.SelectedItem is int c ? c : _app.Settings.DefaultConnections;
        try
        {
            if (_batch != null)
            {
                var chosen = Boxes.Where(b => b.IsChecked == true).Select(b => (AddRequest)b.Tag).ToList();
                if (chosen.Count == 0) return;
                foreach (var r in chosen)
                {
                    r.Folder = folder;
                    r.Connections = connections;
                    r.StartNow = startNow;
                    r.QueueId = SelectedQueue();
                    _app.Manager.Add(r);
                }
            }
            else
            {
                var url = _url.Text.Trim();
                if (BatchPattern.HasPattern(url))
                {
                    // "img[001-120].jpg": open the batch list with every address.
                    var links = BatchPattern.Expand(url).Where(u => FileNames.IsHttpUrl(u, out _))
                        .Select(u => new AddRequest { Url = u, Referrer = _template.Referrer, UserAgent = _template.UserAgent, Folder = folder }).ToList();
                    if (links.Count == 0) { Ui.Info(this, Loc.T("InvalidUrl")); return; }
                    new AddDownloadWindow(_app, links).ShowAndActivate();
                    Close();
                    return;
                }
                if (!FileNames.IsHttpUrl(url, out _)) { Ui.Info(this, Loc.T("InvalidUrl")); _url.Focus(); return; }
                if (_app.Manager.FindDuplicate(url) is { } dup && !AskDuplicate(dup)) return;
                var name = _name.Text.Trim();
                var userChoseName = _nameEdited && name.Length > 0;
                var req = new AddRequest
                {
                    Url = url,
                    // Keep the server's name logic for guesses; an explicit or probed name is used as-is.
                    FileName = userChoseName || _probedName != null ? name : _template.FileName,
                    Folder = folder,
                    Referrer = string.IsNullOrWhiteSpace(_referrer.Text) ? null : _referrer.Text.Trim(),
                    PageUrl = _template.PageUrl,
                    UserAgent = _template.UserAgent,
                    // Cookies only belong to the link they came with.
                    Cookies = url == _template.Url ? _template.Cookies : null,
                    Connections = connections,
                    StartNow = startNow,
                    QueueId = SelectedQueue(),
                    SizeHint = url == _template.Url ? _template.SizeHint : -1,
                    ContentType = _template.ContentType,
                    ExpectedSha256 = string.IsNullOrWhiteSpace(_sha.Text) ? null : _sha.Text.Trim(),
                };
                if (req.ExpectedSha256 != null && (req.ExpectedSha256.Replace(" ", "").Length != 64 || !req.ExpectedSha256.Replace(" ", "").All(Uri.IsHexDigit)))
                {
                    Ui.Info(this, Loc.T("ExpectedSha") + ": ?");
                    return;
                }
                var item = _app.Manager.Add(req);
                if (startNow) _app.ShowProgress(item);
            }
        }
        catch (Exception ex)
        {
            Ui.Info(this, Loc.F("ErrUnknown", ex.Message), "PDM", MessageBoxImage.Error);
            return;
        }
        Close();
    }
}
