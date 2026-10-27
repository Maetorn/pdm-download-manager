using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PDM.Core.Engine;
using PDM.Core.Ipc;
using PDM.Core.Models;
using PDM.Core.Security;
using PDM.Core.Video;

namespace PDM;

/// <summary>Video download: direct media links found by the extension, or yt-dlp with a chosen quality.</summary>
internal sealed class VideoWindow : PdmWindow
{
    private readonly PdmApp _app;
    private readonly string? _userAgent;
    private readonly List<CookieData>? _cookies;
    private readonly TextBox _page = new() { Padding = new Thickness(4) };
    private readonly TextBox _title = new() { Padding = new Thickness(4) };
    private readonly ListBox _media = new() { MaxHeight = 150 };
    private readonly ComboBox _quality = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _container = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _audio, _subs, _playlist;
    private readonly TextBox _folder = new() { Padding = new Thickness(4) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _toolsPanel = new();
    private readonly StackPanel _ytPanel = new();
    private readonly Button _download;
    private CancellationTokenSource? _cts;
    private VideoInfo? _videoInfo;
    /// <summary>The folder follows the download's category (Downloads\Video, \Music) until the user picks one.</summary>
    private bool _autoFolder = true;
    private bool _settingFolder;
    /// <summary>Top page when the video sits in an embedded player frame (sent as Referer).</summary>
    private readonly string? _referrer;

    public VideoWindow(PdmApp app, string pageUrl, string? title, List<MediaLink> media, string? userAgent, List<CookieData>? cookies, string? referrer = null)
    {
        _app = app;
        _userAgent = userAgent;
        _cookies = cookies;
        _referrer = FileNames.IsHttpUrl(referrer, out _) ? referrer : null;
        Title = Loc.T("VideoTitle");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _page.Text = pageUrl;
        _title.Text = CleanTitle(title);
        _audio = Ui.Check(Loc.T("AudioOnly"), false);
        ShowAutoFolder();
        _folder.TextChanged += (_, _) => { if (!_settingFolder) _autoFolder = false; };
        _subs = Ui.Check(Loc.T("Subtitles"), false);
        _playlist = Ui.Check(Loc.T("WholePlaylist"), false);
        _playlist.Visibility = Visibility.Collapsed;
        _info.SetResourceReference(TextBlock.ForegroundProperty, Theme.SubFg);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Field(Loc.T("Page"), _page, 110));
        root.Children.Add(Ui.Field(Loc.T("FileName"), _title, 110));

        // Direct media links
        if (media.Count > 0)
        {
            foreach (var m in media)
            {
                var name = m.Label ?? FileNames.FromUrl(m.Url) ?? m.Url;
                var detail = string.Join(" · ", new[] { m.Mime, m.Size > 0 ? Ui.Size(m.Size) : null }.Where(s => !string.IsNullOrEmpty(s)));
                _media.Items.Add(new CheckBox { Content = new TextBlock { Text = name + (detail.Length > 0 ? "   " + detail : ""), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 }, Tag = m, ToolTip = FileNames.Redact(m.Url) });
            }
            var sp = new StackPanel();
            sp.Children.Add(_media);
            var dl = Ui.Button(Loc.T("DownloadSelected"), (_, _) => DownloadDirect());
            dl.HorizontalAlignment = HorizontalAlignment.Right;
            dl.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(dl);
            // Feeds load many streams that are not this video; keep the list folded then (yt-dlp is the main way).
            root.Children.Add(new Expander
            {
                Header = Loc.F("FoundMediaCount", media.Count),
                Content = new Border { Child = sp, Padding = new Thickness(8, 4, 0, 0) },
                IsExpanded = media.Count <= 3 || !app.Manager.Tools.HasYtDlp,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        // yt-dlp
        foreach (var c in new[] { "mp4", "mkv", "webm" }) _container.Items.Add(c);
        _container.SelectedItem = app.Settings.PreferredContainer is "mkv" or "webm" ? app.Settings.PreferredContainer : "mp4";
        FillQualities(null);
        var getQ = Ui.Button(Loc.T("GetQualities"), (_, _) => _ = AnalyzeAsync());
        var qRow = new StackPanel { Orientation = Orientation.Horizontal };
        qRow.Children.Add(_quality);
        qRow.Children.Add(getQ);
        _ytPanel.Children.Add(Ui.Field(Loc.T("Quality"), qRow, 110));
        _ytPanel.Children.Add(Ui.Field(Loc.T("Container"), _container, 110));
        _ytPanel.Children.Add(_audio);
        _ytPanel.Children.Add(_subs);
        _ytPanel.Children.Add(_playlist);
        _audio.Checked += (_, _) => { _container.IsEnabled = false; ShowAutoFolder(); };
        _audio.Unchecked += (_, _) => { _container.IsEnabled = true; ShowAutoFolder(); };

        _toolsPanel.Children.Add(Ui.Text(Loc.T("ToolsMissing"), wrap: true));
        var install = Ui.Button(Loc.T("InstallTools"), (_, _) => _ = InstallAsync());
        install.HorizontalAlignment = HorizontalAlignment.Left;
        install.Margin = new Thickness(0, 8, 0, 0);
        _toolsPanel.Children.Add(install);

        var yt = new StackPanel();
        yt.Children.Add(_toolsPanel);
        yt.Children.Add(_ytPanel);
        root.Children.Add(new GroupBox { Header = "yt-dlp", Content = yt, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) });

        var browse = Ui.Button(Loc.T("Browse"), (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : app.Settings.DownloadFolder };
            if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
        });
        var folderRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        root.Children.Add(Ui.Field(Loc.T("SaveTo"), folderRow, 110));
        root.Children.Add(_info);
        root.Children.Add(_progress);

        _download = Ui.Button(Loc.T("DownloadNow"), (_, _) => DownloadWithYtDlp(), isDefault: true);
        root.Children.Add(Ui.Buttons(_download, Ui.Button(Loc.T("Cancel"), (_, _) => Close(), isCancel: true)));
        Content = root;

        // No automatic look-up on open: the qualities list works as it is; "Get qualities" asks yt-dlp on demand.
        UpdateToolsVisibility();
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>Where the download will go: the category folder (Video, or Music for sound only) inside Downloads.</summary>
    private void ShowAutoFolder()
    {
        if (!_autoFolder) return;
        var s = _app.Settings;
        _settingFolder = true;
        _folder.Text = s.UseCategoryFolders
            ? Path.Combine(s.DownloadFolder, FileCategories.FolderName(_audio.IsChecked == true ? FileCategory.Music : FileCategory.Video))
            : s.DownloadFolder;
        _settingFolder = false;
    }

    /// <summary>"(3) Some video - YouTube" → "Some video".</summary>
    private static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @"^\(\d+\)\s*", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+[-–|]\s+(YouTube|Vimeo|Dailymotion|Twitch|X|Twitter|Facebook|Instagram)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private void UpdateToolsVisibility()
    {
        var has = _app.Manager.Tools.HasYtDlp;
        _toolsPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _ytPanel.IsEnabled = has;
        _download.IsEnabled = has;
    }

    private void FillQualities(VideoInfo? info)
    {
        _quality.Items.Clear();
        _quality.Items.Add(new ComboBoxItem { Content = Loc.T("Best"), Tag = 0 });
        var heights = info?.Heights is { Count: > 0 } h ? h.Distinct().OrderByDescending(x => x).ToList() : new List<int> { 2160, 1440, 1080, 720, 480, 360 };
        foreach (var height in heights)
        {
            var label = height + "p";
            if (info != null && info.ApproxSizes.TryGetValue(height, out var size) && size > 0) label += "  (~" + Ui.Size(size) + ")";
            _quality.Items.Add(new ComboBoxItem { Content = label, Tag = height });
        }
        var preferred = int.TryParse(_app.Settings.PreferredVideoQuality, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
        // Highest available quality not above the preferred one.
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().Where(i => (int)i.Tag > 0 && (preferred == 0 || (int)i.Tag <= preferred)).OrderByDescending(i => (int)i.Tag).FirstOrDefault()
            ?? _quality.Items[0];
        if (preferred == 0) _quality.SelectedIndex = 0;
    }

    private async Task AnalyzeAsync()
    {
        var url = _page.Text.Trim();
        if (!FileNames.IsHttpUrl(url, out _)) { _info.Text = Loc.T("InvalidUrl"); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _info.Text = Loc.T("Analyzing");
        try
        {
            var cookies = _app.Settings.AcceptBrowserCookies ? _cookies : null;
            var info = await Task.Run(() => YtDlpRunner.GetInfoAsync(_app.Manager.Tools, url, playlist: true, cts.Token, cookies), cts.Token);
            if (cts.IsCancellationRequested) return;
            _videoInfo = info;
            if (string.IsNullOrWhiteSpace(_title.Text)) _title.Text = info.Title;
            FillQualities(info);
            _playlist.Visibility = info.IsPlaylist ? Visibility.Visible : Visibility.Collapsed;
            _info.Text = info.IsPlaylist ? Loc.F("Playlist", info.PlaylistCount) :
                (info.Duration is { } d ? TimeSpan.FromSeconds(d).ToString(d >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : "") + (info.Extractor != null ? "  ·  " + info.Extractor : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts == _cts) _info.Text = Loc.ErrorText(ex.Message.Contains("ytdlp-missing") ? "ytdlp-missing" : ex.Message.Contains("DRM", StringComparison.OrdinalIgnoreCase) ? "drm" : "unknown:" + ex.Message);
        }
    }

    public static async Task InstallToolsAsync(ToolManager tools, Action<double> report, CancellationToken ct)
    {
        // yt-dlp first (small), then FFmpeg (large, needed for merging and conversion), then Deno (YouTube's JavaScript).
        await tools.InstallYtDlpAsync(new Progress<double>(p => report(p * 0.05)), ct);
        await tools.InstallFfmpegAsync(new Progress<double>(p => report(5 + p * 0.7)), ct);
        await tools.InstallDenoAsync(new Progress<double>(p => report(75 + p * 0.25)), ct);
    }

    private async Task InstallAsync()
    {
        _progress.Visibility = Visibility.Visible;
        _toolsPanel.IsEnabled = false;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await InstallToolsAsync(_app.Manager.Tools, p => { _progress.Value = p; _info.Text = Loc.F("Installing", p); }, cts.Token);
            _info.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _info.Text = Loc.F("ErrUnknown", ex.Message);
        }
        _progress.Visibility = Visibility.Collapsed;
        _toolsPanel.IsEnabled = true;
        UpdateToolsVisibility();
    }

    private string? Folder()
    {
        var f = _folder.Text.Trim();
        if (Path.IsPathFullyQualified(f)) return f;
        Ui.Info(this, Loc.T("SaveTo") + ": ?");
        return null;
    }

    private void DownloadDirect()
    {
        // Direct files go to the folder of their own type (an .mp3 to Music), unless a folder was chosen.
        var folder = _autoFolder ? _app.Settings.DownloadFolder : Folder();
        if (folder == null) return;
        var chosen = _media.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (MediaLink)c.Tag).ToList();
        if (chosen.Count == 0) return;
        var page = FileNames.IsHttpUrl(_page.Text.Trim(), out _) ? _page.Text.Trim() : null;
        var referrer = _referrer ?? page;
        if (chosen.Any(IsStreamManifest) && !_app.Manager.Tools.HasYtDlp)
        {
            Ui.Info(this, Loc.T("ToolsMissing"));
            return;
        }
        DownloadItem? last = null;
        foreach (var m in chosen)
        {
            if (IsStreamManifest(m))
            {
                // HLS/DASH: the file is a playlist of small pieces; yt-dlp + FFmpeg join them into one video.
                var container = _container.SelectedItem as string ?? "mp4";
                var title = string.IsNullOrWhiteSpace(_title.Text) ? (FileNames.FromUrl(page) ?? "video") : _title.Text.Trim();
                last = _app.Manager.Add(new AddRequest
                {
                    Kind = DownloadKind.Video,
                    Url = m.Url,
                    PageUrl = page,
                    Referrer = referrer,
                    FileName = title + "." + container,
                    Folder = folder,
                    Connections = 1,
                    Cookies = _cookies,
                    Video = new VideoOptions { Format = YtDlpRunner.FormatFor(null, container), Container = container, Label = "HLS/DASH" },
                });
                continue;
            }
            string? name = null;
            if (chosen.Count == 1 && !string.IsNullOrWhiteSpace(_title.Text))
            {
                var ext = Path.GetExtension(FileNames.FromUrl(m.Url) ?? "");
                name = _title.Text.Trim() + (string.IsNullOrEmpty(Path.GetExtension(_title.Text.Trim())) ? ext : "");
            }
            last = _app.Manager.Add(new AddRequest
            {
                Url = m.Url,
                FileName = string.IsNullOrWhiteSpace(name) ? null : name,
                Folder = folder,
                Referrer = referrer,
                PageUrl = page,
                UserAgent = _userAgent,
                Cookies = _cookies,
                SizeHint = m.Size,
                ContentType = m.Mime,
            });
        }
        if (chosen.Count == 1 && last != null) _app.ShowProgress(last);
        Close();
    }

    private static bool IsStreamManifest(MediaLink m)
    {
        var ext = Path.GetExtension(FileNames.FromUrl(m.Url) ?? "").ToLowerInvariant();
        var mime = (m.Mime ?? "").ToLowerInvariant();
        return ext is ".m3u8" or ".mpd" || mime.Contains("mpegurl") || mime.Contains("dash+xml");
    }

    private void DownloadWithYtDlp()
    {
        var url = _page.Text.Trim();
        if (!FileNames.IsHttpUrl(url, out _)) { Ui.Info(this, Loc.T("InvalidUrl")); return; }
        var folder = Folder();
        if (folder == null) return;
        var height = _quality.SelectedItem is ComboBoxItem { Tag: int h } ? h : 0;
        var audio = _audio.IsChecked == true;
        var container = _container.SelectedItem as string ?? "mp4";
        var options = new VideoOptions
        {
            Format = audio ? "ba/b" : YtDlpRunner.FormatFor(height > 0 ? height : null, container),
            Container = audio ? "" : container,
            AudioOnly = audio,
            AudioFormat = "m4a",
            Playlist = _playlist.IsChecked == true && _videoInfo?.IsPlaylist == true,
            Subtitles = _subs.IsChecked == true,
            Label = audio ? Loc.T("AudioOnly") : height > 0 ? height + "p" : Loc.T("Best"),
        };
        var title = string.IsNullOrWhiteSpace(_title.Text) ? (_videoInfo?.Title ?? "video") : _title.Text.Trim();
        var ext = audio ? ".m4a" : "." + (string.IsNullOrEmpty(options.Container) ? "mp4" : options.Container);
        try
        {
            var item = _app.Manager.Add(new AddRequest
            {
                Kind = DownloadKind.Video,
                Url = url,
                PageUrl = url,
                Referrer = _referrer ?? url,
                FileName = title + ext,
                Folder = folder,
                Video = options,
                Cookies = _cookies,
                StartNow = true,
            });
            _app.ShowProgress(item);
        }
        catch (Exception ex)
        {
            Ui.Info(this, Loc.F("ErrUnknown", ex.Message), "PDM", MessageBoxImage.Error);
            return;
        }
        Close();
    }
}
