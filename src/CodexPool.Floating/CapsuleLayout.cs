using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;
using Color = System.Windows.Media.Color;

namespace CodexPool.Floating;

internal static class CapsuleLayout
{
    internal static Border Account(string name, string status, string tooltip, bool active, bool selectable,
        Brush foreground, Brush accent, Brush edge, AccountElements? shared = null, string? five = null, string? week = null)
    {
        var textColor = !selectable ? SystemColors.GrayTextBrush : active ? Brushes.White : foreground;
        shared ??= new AccountElements();
        shared.Detach();
        var body = new StackPanel();
        shared.Name.Text = name; shared.Name.FontSize = 12; shared.Name.MaxWidth = 100;
        shared.Name.FontWeight = FontWeights.Normal; shared.Name.Foreground = textColor;
        shared.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(shared.Name);
        if (five is not null && week is not null)
        {
            var quota = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            shared.FiveValue.Text = five; shared.WeekValue.Text = week;
            foreach (var part in new[] { shared.FiveHeading, shared.FiveValue, shared.WeekHeading, shared.WeekValue })
            { part.FontSize = 10; part.Foreground = textColor; part.Opacity = .75; }
            shared.FiveHeading.Margin = shared.WeekHeading.Margin = new Thickness(0, 0, 3, 0);
            quota.Children.Add(shared.FiveHeading); quota.Children.Add(shared.FiveValue);
            quota.Children.Add(new TextBlock { Text = " · ", FontSize = 10, Foreground = textColor, Opacity = .75 });
            quota.Children.Add(shared.WeekHeading); quota.Children.Add(shared.WeekValue);
            body.Children.Add(quota);
        }
        else body.Children.Add(new TextBlock { Text = status, FontSize = 10, Foreground = textColor, Opacity = .75, Margin = new Thickness(0, 2, 0, 0) });
        var chip = shared.Card;
        chip.Child = body; chip.Padding = new Thickness(9, 4, 9, 4); chip.Margin = new Thickness(2, 0, 2, 0);
        chip.CornerRadius = new CornerRadius(14); chip.BorderThickness = new Thickness(1);
        chip.BorderBrush = active ? accent : edge;
        chip.Background = !selectable ? new SolidColorBrush(Color.FromArgb(28, 135, 150, 170)) : active ? accent : Brushes.Transparent;
        chip.ToolTip = tooltip; chip.Cursor = selectable && !active ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        chip.IsEnabled = selectable; chip.Opacity = selectable ? 1 : .55;
        ToolTipService.SetShowOnDisabled(chip, true);
        return chip;
    }
}
