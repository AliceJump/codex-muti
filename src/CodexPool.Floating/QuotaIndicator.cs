using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace CodexPool.Floating;

internal static class QuotaIndicator
{
    public static bool IsExhausted(double? fiveHourRemaining, double? weeklyRemaining) =>
        fiveHourRemaining is <= 0.5 || weeklyRemaining is <= 0.5;

    public static StreamGeometry Ring(double percent, double size, double thickness)
    {
        var geometry = new StreamGeometry();
        if (double.IsFinite(percent) && percent > 0)
        {
            using var context = geometry.Open();
            var radius = (size - thickness) / 2;
            var center = new Point(size / 2, size / 2);
            var start = new Point(center.X, center.Y - radius);
            context.BeginFigure(start, false, false);
            var sweep = Math.Clamp(percent, 0, 100) / 100 * 360;
            if (sweep >= 360)
            {
                // WPF treats a single arc with coincident endpoints as empty.
                context.ArcTo(new(center.X, center.Y + radius), new(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.ArcTo(start, new(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                var radians = (sweep - 90) * Math.PI / 180;
                var end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
                context.ArcTo(end, new(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
        }
        geometry.Freeze(); return geometry;
    }
}
