using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Panel = System.Windows.Controls.Panel;

namespace CodexPool.Floating;

// These elements survive all three layouts; only their containers and styling change.
internal sealed class AccountElements
{
    internal Dictionary<string, WindowElements> Windows { get; } = new();
    internal WindowElements Window(DisplayQuotaWindow display)
    {
        if (!Windows.TryGetValue(display.Key, out var row))
        {
            row = display.Kind == "5h" && display.Key.StartsWith("codex:", StringComparison.Ordinal) && Windows.Values.All(x => !ReferenceEquals(x.Value, FiveValue))
                ? new(FiveHeading, FiveValue, FiveReset, FiveTrack)
                : display.Kind == "weekly" && display.Key.StartsWith("codex:", StringComparison.Ordinal) && Windows.Values.All(x => !ReferenceEquals(x.Value, WeekValue))
                    ? new(WeekHeading, WeekValue, WeekReset, WeekTrack) : new();
            Windows[display.Key] = row;
        }
        return row;
    }
    internal Border Card { get; } = new();
    internal TextBlock Name { get; } = new();
    internal TextBlock FiveHeading { get; } = new() { Text = "5h" };
    internal TextBlock FiveValue { get; } = new();
    internal TextBlock WeekHeading { get; } = new() { Text = "周" };
    internal TextBlock WeekValue { get; } = new();
    internal TextBlock Plan { get; } = new();
    internal TextBlock Updated { get; } = new();
    internal TextBlock Tokens { get; } = new();
    internal TextBlock FiveReset { get; } = new();
    internal TextBlock WeekReset { get; } = new();
    internal ProgressBar FiveTrack { get; } = new();
    internal ProgressBar WeekTrack { get; } = new();
    internal IEnumerable<FrameworkElement> Parts => new FrameworkElement[]
        { Name, FiveHeading, FiveValue, WeekHeading, WeekValue, Plan, Updated, Tokens, FiveReset, WeekReset, FiveTrack, WeekTrack }
        .Concat(Windows.Values.SelectMany(x => x.Parts)).Distinct();
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
internal sealed class WindowElements
{
    internal TextBlock Heading { get; }
    internal TextBlock Value { get; }
    internal TextBlock Reset { get; }
    internal ProgressBar Track { get; }
    internal WindowElements(TextBlock? heading = null, TextBlock? value = null, TextBlock? reset = null, ProgressBar? track = null)
    { Heading = heading ?? new(); Value = value ?? new(); Reset = reset ?? new(); Track = track ?? new(); }
    internal IEnumerable<FrameworkElement> Parts => new FrameworkElement[] { Heading, Value, Reset, Track };
}
