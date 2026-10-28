using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using ToolTip = System.Windows.Controls.ToolTip;
using SystemColors = System.Windows.SystemColors;
using Control = System.Windows.Controls.Control;
using FontFamily = System.Windows.Media.FontFamily;

namespace CodexPool.Floating;

internal static class InfoTip
{
    private const string StyleKey = "PoolInfoTipStyle";

    internal static void SetTheme(FrameworkElement root, bool light, bool contrast)
    {
        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(Control.BackgroundProperty, contrast ? SystemColors.InfoBrush : new Brush(light ? Color.FromRgb(247, 249, 253) : Color.FromRgb(30, 39, 53))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, contrast ? SystemColors.InfoTextBrush : new Brush(light ? Color.FromRgb(29, 41, 59) : Color.FromRgb(239, 244, 253))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, contrast ? SystemColors.WindowTextBrush : new Brush(light ? Color.FromRgb(203, 213, 225) : Color.FromRgb(76, 92, 115))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        style.Setters.Add(new Setter(Control.TemplateProperty, Template()));
        root.Resources[StyleKey] = style;
    }

    private static ControlTemplate Template()
    {
        var template = new ControlTemplate(typeof(ToolTip));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        text.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.MaxWidthProperty, 340d);
        text.SetValue(TextBlock.LineHeightProperty, 19d);
        border.AppendChild(text); template.VisualTree = border;
        return template;
    }

    internal static void Set(FrameworkElement owner, string content)
    {
        if (owner.ToolTip is ToolTip existing) { existing.Content = content; return; }
        var tip = new ToolTip { Content = content, PlacementTarget = owner, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
        tip.Opened += (_, _) => tip.Style = owner.TryFindResource(StyleKey) as Style;
        owner.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(owner, 450);
        ToolTipService.SetShowDuration(owner, 30000);
        ToolTipService.SetShowOnDisabled(owner, true);
    }
}
