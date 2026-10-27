#if UI_TOUR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDM.Core.Engine;
using PDM.Core.Ipc;
using PDM.Core.Models;
using PDM.Core.Video;

namespace PDM;

/// <summary>
/// Test build only (dotnet build -p:UiTour=true; never in a release): "PDM.exe --ui-tour &lt;dir&gt; &lt;lang&gt; &lt;theme&gt;"
/// opens every window in that language and theme, saves a PNG of each and writes clipped.txt with every piece of
/// text that does not fit its space. Used by .github/workflows/ui-tour.yml.
/// </summary>
internal static class UiTour
{
    public static bool Requested(string[] args, out string dir, out string lang, out string theme)
    {
        var i = Array.IndexOf(args, "--ui-tour");
        dir = lang = theme = "";
        if (i < 0 || i + 3 >= args.Length) return false;
        (dir, lang, theme) = (args[i + 1], args[i + 2], args[i + 3]);
        return true;
    }

    private static readonly StringBuilder Report = new();
    private static string _dir = "";
    private static int _n;

    public static async Task RunAsync(PdmApp app, string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        try
        {
            await TourAsync(app);
        }
        catch (Exception ex)
        {
            Report.AppendLine("TOUR FAILED\t" + ex);
        }
        File.WriteAllText(Path.Combine(dir, "clipped.txt"), Report.ToString());
        Environment.Exit(0);
    }

    private static async Task TourAsync(PdmApp app)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdm-tour");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Big Buck Bunny 1080p.mp4");
        File.WriteAllBytes(file, new byte[256 * 1024]);
        var done = new DownloadItem
        {
            Url = "https://download.example.org/videos/big-buck-bunny-1080p.mp4", FileName = Path.GetFileName(file), Folder = folder,
            TotalBytes = 256 * 1024, DownloadedBytes = 256 * 1024, Status = DownloadStatus.Completed, CompletedAt = DateTime.Now, SupportsResume = true,
        };
        var paused = new DownloadItem
        {
            Url = "https://mirror.example.net/iso/ubuntu-24.04-desktop-amd64.iso", FileName = "ubuntu-24.04-desktop-amd64.iso", Folder = folder,
            TotalBytes = 6_114_656_256, DownloadedBytes = 2_401_000_000, Status = DownloadStatus.Paused, SupportsResume = true, Connections = 8,
        };
        var failed = new DownloadItem
        {
            Url = "https://files.example.com/reports/annual-report-2025.pdf", FileName = "annual-report-2025.pdf", Folder = folder,
            TotalBytes = 3_400_000, DownloadedBytes = 1_200_000, Status = DownloadStatus.Error, Error = "http:404",
        };
        var later = new DownloadItem
        {
            Url = "https://cdn.example.org/tools/setup-tool-x64.exe", FileName = "setup-tool-x64.exe", Folder = folder,
            TotalBytes = 48_000_000, Status = DownloadStatus.Paused, QueueId = QueueInfo.MainId,
        };
        foreach (var it in new[] { later, failed, paused, done }) app.Manager.Items.Insert(0, it);

        app.ShowMain();
        await Shot(app.Main, "main");
        ContextMenu? opened = null;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => opened = (ContextMenu)s));
        var grid = Descendants(app.Main).OfType<DataGrid>().First();
        grid.SelectedItem = paused;
        await Menu(() => { grid.ContextMenu!.PlacementTarget = grid; grid.ContextMenu.IsOpen = true; }, () => opened, "menu-list");
        var more = Descendants(app.Main).OfType<Button>().Last(b => b.ToolTip as string == Loc.T("More"));
        await Menu(() => typeof(MainWindow).GetMethod("ShowMoreMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app.Main, [more]), () => opened, "menu-more");
        app.Main.Hide();

        var add = new AddDownloadWindow(app, new AddRequest { Url = "https://download.example.org/files/sample-archive.zip" });
        add.Show();
        await Shot(add, "add", 2500); // the address check answers (404 from example.org)
        await Modal(() => typeof(AddDownloadWindow).GetMethod("AskDuplicate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(add, [done]), "add-duplicate");
        add.Close();

        var batch = new AddDownloadWindow(app, Enumerable.Range(1, 3).Select(i => new AddRequest { Url = $"https://download.example.org/photos/holiday-{i:000}.jpg" }).ToList());
        batch.Show();
        await Shot(batch, "add-batch", 800);
        batch.Close();

        var settings = new SettingsWindow(app);
        settings.Show();
        await Idle();
        var tabs = Descendants(settings).OfType<TabControl>().First();
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            await Shot(settings, "settings-" + i, 400);
        }
        tabs.SelectedIndex = 3; // site logins
        await Idle();
        await Modal(() => LoginDialog.Edit(settings, null, "example.org"), "login");
        settings.Close();

        await ShowShot(new SchedulerWindow(app), "scheduler");
        HistoryWindow.ShowFor(app, app.Main);
        var history = Application.Current.Windows.OfType<HistoryWindow>().First();
        await Shot(history, "history");
        history.Close();

        await ShowShot(new VideoWindow(app, "https://www.youtube.com/watch?v=aqz-KE-bpKQ", "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film",
            [new MediaLink("https://cdn.example.org/media/big-buck-bunny-1080p.mp4", "video/mp4", 52_428_800, "1080p")], null, null), "video");
        await ShowShot(new GrabberWindow(app, "https://example.org/downloads/"), "grabber");
        await ShowShot(new DetailsWindow(app, paused), "details");
        await ShowShot(new ProgressWindow(app, paused), "progress");
        await ShowShot(new CompleteWindow(app, done), "complete");
        await ShowShot(new ConvertWindow(app, [done], ConvertTarget.Mp3), "convert", 800);
        await ShowShot(new CountdownWindow(app, PostQueueAction.Sleep), "countdown");
        await ShowShot(new ExtensionGuideWindow(), "extension-guide");
        await ShowShot(new SetupWindow(), "setup");
    }

    private static async Task ShowShot(Window w, string name, int waitMs = 500)
    {
        w.Show();
        await Shot(w, name, waitMs);
        w.Close();
    }

    /// <summary>Opens a modal dialog (it runs its own loop), captures and closes it from a timer.</summary>
    private static async Task Modal(Action open, string name)
    {
        var before = Application.Current.Windows.OfType<Window>().ToHashSet();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => !before.Contains(x) && x.IsVisible);
            if (w == null) { Report.AppendLine(name + "\tNO WINDOW"); return; }
            Capture(w, name);
            w.Close();
        };
        timer.Start();
        open();
        await Idle();
    }

    private static async Task Menu(Action open, Func<ContextMenu?> current, string name)
    {
        open();
        await Task.Delay(400);
        await Idle();
        if (current() is not { IsOpen: true } menu) { Report.AppendLine(name + "\tNO MENU"); return; }
        Capture(menu, menu.Background, name, "(menu)");
        menu.IsOpen = false;
        await Idle();
    }

    private static async Task Shot(Window w, string name, int waitMs = 500)
    {
        await Idle();
        await Task.Delay(waitMs);
        await Idle();
        Capture(w, name);
    }

    private static Task Idle() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void Capture(Window w, string name)
    {
        w.UpdateLayout();
        if (w.Content is not FrameworkElement root || root.ActualWidth < 1) { Report.AppendLine(name + "\tEMPTY"); return; }
        Capture(root, w.Background, name, w.Title);
    }

    private static void Capture(FrameworkElement root, Brush? background, string name, string title)
    {
        var file = Path.Combine(_dir, $"{++_n:00}-{name}.png");
        CheckFit(root, name);
        var dpi = VisualTreeHelper.GetDpi(root);
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }
        rtb.Render(dv);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        png.Save(fs);
        Report.AppendLine($"{name}\tWINDOW\t{title}");
    }

    /// <summary>Lists every visible text that is cut: wider than its own box, or outside a parent's visible area.</summary>
    private static void CheckFit(FrameworkElement root, string name)
    {
        var ppd = VisualTreeHelper.GetDpi(root).PixelsPerDip;
        foreach (var tb in Descendants(root).OfType<TextBlock>())
        {
            if (!tb.IsVisible || tb.ActualWidth <= 0 || string.IsNullOrWhiteSpace(tb.Text)) continue;
            if (Ancestors(tb).Any(a => a is DataGridRow)) continue; // list rows: long file names are trimmed on purpose
            var text = tb.Text.Replace('\n', ' ');
            if (tb.TextWrapping == TextWrapping.NoWrap)
            {
                var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
                    null, TextOptions.GetTextFormattingMode(tb), ppd);
                var need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
                if (need > tb.ActualWidth + 1) Report.AppendLine($"{name}\tCUT\t{need:0}>{tb.ActualWidth:0}\t{text}");
            }
            // Parents that clip (fixed-width buttons, grid columns, the window itself).
            var scrolled = false; // past a scroll viewer, text below the visible part is only scrolled away
            // The text block itself first: a label wider than its grid column is clipped at the column's edge.
            foreach (var a in Ancestors(tb).Prepend(tb).OfType<FrameworkElement>())
            {
                var clip = LayoutInformation.GetLayoutClip(a)?.Bounds
                    ?? (a == root || a.ClipToBounds ? new Rect(0, 0, a.ActualWidth, a.ActualHeight) : Rect.Empty);
                if (clip.IsEmpty) { if (a == root) break; continue; }
                var box = tb.TransformToAncestor(a).TransformBounds(new Rect(tb.RenderSize));
                var vertical = !scrolled && a is not ScrollContentPresenter && (box.Bottom > clip.Bottom + 1 || box.Top < clip.Top - 1);
                if (a is ScrollContentPresenter) scrolled = true;
                if (box.Right > clip.Right + 1 || box.Left < clip.Left - 1 || vertical)
                {
                    Report.AppendLine($"{name}\tOUTSIDE\t{a.GetType().Name} {clip.Width:0}x{clip.Height:0} < {box.Right:0},{box.Bottom:0}\t{text}");
                    break;
                }
                if (a == root) break;
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }
}
#endif
