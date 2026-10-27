using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDM;

/// <summary>Base for all PDM windows: theme brushes, icon, dark title bar.</summary>
internal class PdmWindow : Window
{
    private static ImageSource? _icon;

    public PdmWindow()
    {
        SetResourceReference(BackgroundProperty, Theme.Bg);
        SetResourceReference(ForegroundProperty, Theme.Fg);
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = _icon ??= BitmapFrame.Create(new Uri("pack://application:,,,/Assets/pdm.ico")); } catch { }
        SourceInitialized += (_, _) => Native.SetDarkTitleBar(this, Theme.IsDark);
    }

    /// <summary>Owner when it is visible; null otherwise (an owner hidden in the tray would hide the dialog too).</summary>
    public static Window? VisibleOwner() =>
        Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
}

internal static class Ui
{
    public static TextBlock Text(string s, double size = 13, bool bold = false, string? brush = null, bool wrap = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    public static Button Button(string text, RoutedEventHandler? click = null, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 84, Margin = new Thickness(6, 0, 0, 0) };
        if (click != null) b.Click += click;
        return b;
    }

    /// <summary>A toolbar button: Segoe MDL2 glyph above a caption.</summary>
    public static Button ToolButton(string glyph, string caption, RoutedEventHandler click)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
        sp.Children.Add(g);
        sp.Children.Add(new TextBlock { Text = caption, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        var b = new Button { Content = sp, Padding = new Thickness(8, 6, 8, 4), Margin = new Thickness(2), MinWidth = 64, BorderThickness = new Thickness(0), ToolTip = caption };
        b.SetResourceReference(Control.BackgroundProperty, Theme.Panel);
        b.SetResourceReference(Control.BorderBrushProperty, Theme.Panel);
        b.Click += click;
        return b;
    }

    public static StackPanel Buttons(params UIElement[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) sp.Children.Add(b);
        return sp;
    }

    /// <summary>Label on the left, control on the right, as one grid row.</summary>
    public static Grid Field(string label, UIElement control, double labelWidth = 150)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = Text(label);
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static CheckBox Check(string text, bool value) =>
        new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };

    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border { Child = child, Padding = new Thickness(12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = margin ?? new Thickness(0, 0, 0, 10) };
        b.SetResourceReference(Border.BackgroundProperty, Theme.Panel);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
        return b;
    }

    public static string Size(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " + units[u];
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Size((long)bytesPerSecond) + "/s";

    public static string Eta(TimeSpan? t)
    {
        if (t is not { } v) return "";
        if (v.TotalDays >= 1) return $"{(int)v.TotalDays}g {v.Hours}s";
        if (v.TotalHours >= 1) return $"{(int)v.TotalHours}:{v.Minutes:00}:{v.Seconds:00}";
        return $"{v.Minutes}:{v.Seconds:00}";
    }

    public static bool Confirm(Window? owner, string text, string title, MessageBoxImage icon = MessageBoxImage.Question) =>
        (owner != null ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, icon) : MessageBox.Show(text, title, MessageBoxButton.YesNo, icon)) == MessageBoxResult.Yes;

    public static void Info(Window? owner, string text, string title = "PDM", MessageBoxImage icon = MessageBoxImage.Information)
    {
        if (owner != null) MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(text, title, MessageBoxButton.OK, icon);
    }

    /// <summary>Simple one-line text prompt.</summary>
    public static string? Prompt(Window? owner, string title, string message, string initial = "")
    {
        var w = new PdmWindow { Title = title, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Owner = owner };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4) };
        string? result = null;
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Text(message, wrap: true));
        sp.Children.Add(box);
        sp.Children.Add(Buttons(Button(Loc.T("Ok"), (_, _) => { result = box.Text; w.DialogResult = true; }, isDefault: true), Button(Loc.T("Cancel"), isCancel: true)));
        w.Content = sp;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }
}
