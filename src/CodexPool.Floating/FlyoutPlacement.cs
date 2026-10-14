using System.Drawing;

namespace CodexPool.Floating;

internal static class FlyoutPlacement
{
    internal static Point Beside(Rectangle workArea, Rectangle anchor, int width, int height, int gap)
    {
        var x = anchor.Right + gap;
        if (x + width > workArea.Right) x = anchor.Left - width - gap;
        return new Point(Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)),
            Math.Clamp(anchor.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height)));
    }
    internal static Point Above(Rectangle workArea, Rectangle anchor, int width, int height, int gap)
    {
        var x = anchor.Left + anchor.Width / 2 - width / 2;
        var y = anchor.Top - height - gap;
        // A top taskbar/overflow near the top has no room above; keep the panel on-screen.
        if (y < workArea.Top && anchor.Bottom + gap + height <= workArea.Bottom) y = anchor.Bottom + gap;
        return new Point(
            Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height)));
    }
}
