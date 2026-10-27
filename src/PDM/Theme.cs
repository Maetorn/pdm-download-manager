using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace PDM;

/// <summary>Light/dark brushes defined in code and exposed as application resources (DynamicResource keys).</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public const string Bg = "PdmBg", Panel = "PdmPanel", Fg = "PdmFg", SubFg = "PdmSubFg", Accent = "PdmAccent",
        Border = "PdmBorder", Sel = "PdmSel", Input = "PdmInput", Hover = "PdmHover", Good = "PdmGood", Bad = "PdmBad", Track = "PdmTrack";

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(Application app, string mode)
    {
        IsDark = mode switch { "Dark" => true, "Light" => false, _ => SystemUsesDark() };
        var r = app.Resources;
        void Set(string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            r[key] = b;
        }
        if (IsDark)
        {
            Set(Bg, "#1E1F22"); Set(Panel, "#2B2D31"); Set(Fg, "#E8E8E8"); Set(SubFg, "#A0A4AB"); Set(Accent, "#4C9AFF");
            Set(Border, "#3C3F45"); Set(Sel, "#2F4F7A"); Set(Input, "#26282C"); Set(Hover, "#34373D"); Set(Good, "#4CC38A"); Set(Bad, "#FF6B6B"); Set(Track, "#3A3D42");
        }
        else
        {
            Set(Bg, "#FFFFFF"); Set(Panel, "#F3F4F6"); Set(Fg, "#1B1B1F"); Set(SubFg, "#5F6670"); Set(Accent, "#1A73E8");
            Set(Border, "#D6D9DE"); Set(Sel, "#CCE0FF"); Set(Input, "#FFFFFF"); Set(Hover, "#E6E9EE"); Set(Good, "#188038"); Set(Bad, "#C5221F"); Set(Track, "#E3E5E8");
        }

        r[typeof(Window)] = Style(typeof(Window), (Control.BackgroundProperty, Bg), (Control.ForegroundProperty, Fg));
        r[typeof(TextBlock)] = Style(typeof(TextBlock), (TextBlock.ForegroundProperty, Fg));
        r[typeof(Label)] = Style(typeof(Label), (Control.ForegroundProperty, Fg));
        r[typeof(CheckBox)] = Style(typeof(CheckBox), (Control.ForegroundProperty, Fg));
        r[typeof(RadioButton)] = Style(typeof(RadioButton), (Control.ForegroundProperty, Fg));
        r[typeof(GroupBox)] = Style(typeof(GroupBox), (Control.ForegroundProperty, Fg), (Control.BorderBrushProperty, Border));
        r[typeof(TextBox)] = Style(typeof(TextBox), (Control.BackgroundProperty, Input), (Control.ForegroundProperty, Fg), (Control.BorderBrushProperty, Border), (TextBox.CaretBrushProperty, Fg));
        r[typeof(PasswordBox)] = Style(typeof(PasswordBox), (Control.BackgroundProperty, Input), (Control.ForegroundProperty, Fg), (Control.BorderBrushProperty, Border), (PasswordBox.CaretBrushProperty, Fg));
        r[typeof(ListBox)] = Style(typeof(ListBox), (Control.BackgroundProperty, Input), (Control.ForegroundProperty, Fg), (Control.BorderBrushProperty, Border));
        r[typeof(ListBoxItem)] = Style(typeof(ListBoxItem), (Control.ForegroundProperty, Fg));
        r[typeof(TabControl)] = Style(typeof(TabControl), (Control.BackgroundProperty, Bg), (Control.BorderBrushProperty, Border));
        r[typeof(Slider)] = Style(typeof(Slider), (Control.ForegroundProperty, Accent));
        r[typeof(ProgressBar)] = Style(typeof(ProgressBar), (Control.ForegroundProperty, Accent), (Control.BackgroundProperty, Track), (Control.BorderThicknessProperty, null));
        r[typeof(StatusBar)] = Style(typeof(StatusBar), (Control.BackgroundProperty, Panel), (Control.ForegroundProperty, SubFg));
        r[typeof(DataGrid)] = Style(typeof(DataGrid), (Control.BackgroundProperty, Bg), (Control.ForegroundProperty, Fg), (Control.BorderBrushProperty, Border),
            (DataGrid.RowBackgroundProperty, Bg), (DataGrid.AlternatingRowBackgroundProperty, Bg), (DataGrid.HorizontalGridLinesBrushProperty, Border), (DataGrid.VerticalGridLinesBrushProperty, Border));
        r[typeof(DataGridColumnHeader)] = Style(typeof(DataGridColumnHeader), (Control.BackgroundProperty, Panel), (Control.ForegroundProperty, SubFg), (Control.BorderBrushProperty, Border));

        var cell = Style(typeof(DataGridCell), (Control.ForegroundProperty, Fg));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Sel)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Fg)));
        cell.Triggers.Add(selected);
        r[typeof(DataGridCell)] = cell;

        // Buttons: flat, themed, with hover.
        var button = Style(typeof(Button), (Control.BackgroundProperty, Panel), (Control.ForegroundProperty, Fg), (Control.BorderBrushProperty, Border));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 4, 12, 4)));
        button.Setters.Add(new Setter(Control.TemplateProperty, ButtonTemplate()));
        r[typeof(Button)] = button;

        if (IsDark)
        {
            // Default ComboBox chrome ignores Background; keep the item text readable on its light popup.
            // Styles are finished before they go into the resources: WPF seals a style once it is in use.
            var combo = Style(typeof(ComboBox));
            combo.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            r[typeof(ComboBox)] = combo;
            var comboItem = Style(typeof(ComboBoxItem));
            comboItem.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            r[typeof(ComboBoxItem)] = comboItem;
        }
    }

    private static Style Style(Type t, params (DependencyProperty Prop, string? Key)[] setters)
    {
        var s = new Style(t);
        foreach (var (p, k) in setters)
        {
            if (k == null) s.Setters.Add(new Setter(p, new Thickness(0)));
            else s.Setters.Add(new Setter(p, new DynamicResourceExtension(k)));
        }
        return s;
    }

    private static ControlTemplate ButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "Bd");
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(System.Windows.Controls.Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(System.Windows.Controls.Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new DynamicResourceExtension(Hover), "Bd"));
        hover.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(Accent), "Bd"));
        t.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        t.Triggers.Add(disabled);
        var def = new Trigger { Property = Button.IsDefaultProperty, Value = true };
        def.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(Accent), "Bd"));
        t.Triggers.Add(def);
        return t;
    }
}
