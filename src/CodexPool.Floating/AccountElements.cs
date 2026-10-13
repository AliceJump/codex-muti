using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Panel = System.Windows.Controls.Panel;

namespace CodexPool.Floating;

// These elements survive all three layouts; only their containers and styling change.
internal sealed class AccountElements
{
    internal Border Card { get; } = new();
    internal TextBlock Name { get; } = new();
    internal TextBlock FiveHeading { get; } = new() { Text = "5h" };
    internal TextBlock FiveValue { get; } = new();
    internal TextBlock WeekHeading { get; } = new() { Text = "周" };
    internal TextBlock WeekValue { get; } = new();
    internal TextBlock Plan { get; } = new();
    internal TextBlock Updated { get; } = new();
    internal TextBlock FiveReset { get; } = new();
    internal TextBlock WeekReset { get; } = new();
    internal ProgressBar FiveTrack { get; } = new();
    internal ProgressBar WeekTrack { get; } = new();
    internal IEnumerable<FrameworkElement> Parts => new FrameworkElement[]
        { Name, FiveHeading, FiveValue, WeekHeading, WeekValue, Plan, Updated, FiveReset, WeekReset, FiveTrack, WeekTrack };
    internal void Detach()
    {
        if (Card.Parent is Panel parent) parent.Children.Remove(Card);
        foreach (var part in Parts)
        {
            if (part.Parent is Panel panel) panel.Children.Remove(part);
            part.Margin = new Thickness();
            part.RenderTransform = Transform.Identity;
            Grid.SetColumn(part, 0);
        }
    }
}
