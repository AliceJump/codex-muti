using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace CodexPool.Floating;

internal static class CapsuleLayout
{
    internal static Border Account(string name, string status, string tooltip, bool active, bool selectable,
        Brush foreground, Brush accent, Brush edge)
    {
        var textColor = active ? Brushes.White : foreground;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = active ? "◉" : "○", FontSize = 12, Foreground = textColor,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = name, FontSize = 12, MaxWidth = 100, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = textColor
        });
        var body = new StackPanel();
        body.Children.Add(row);
        body.Children.Add(new TextBlock { Text = status, FontSize = 10, Foreground = textColor, Opacity = .75, Margin = new Thickness(0, 2, 0, 0) });
        return new Border
        {
            Child = body, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            BorderBrush = active ? accent : edge,
            Background = active ? accent : Brushes.Transparent,
            ToolTip = tooltip, Cursor = selectable && !active ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            Opacity = selectable || active ? 1 : .5
        };
    }
}
