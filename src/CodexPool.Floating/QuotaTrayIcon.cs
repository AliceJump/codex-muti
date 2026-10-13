using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexPool.Floating;

internal static class QuotaTrayIcon
{
    // Native tray icons are tiny: use the number itself, and explain % / both windows in the tooltip.
    public static Icon Create(double? usedPercent, double threshold = 95)
    {
        var known = usedPercent is { } value && double.IsFinite(value);
        var used = known ? Math.Clamp(usedPercent!.Value, 0, 100) : 0;
        var accent = !known ? Color.FromArgb(150, 161, 177)
            : used >= threshold ? Color.FromArgb(255, 112, 105)
            : used >= Math.Max(0, threshold - 10) ? Color.FromArgb(255, 198, 95)
            : Color.FromArgb(117, 219, 175);
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var background = new SolidBrush(Color.FromArgb(24, 33, 48));
        graphics.FillEllipse(background, 0, 0, 32, 32);
        using var track = new Pen(Color.FromArgb(70, 83, 100), 2.5f);
        graphics.DrawEllipse(track, 2.5f, 2.5f, 27, 27);
        using var stroke = new Pen(accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (known && used > 0) graphics.DrawArc(stroke, 2.5f, 2.5f, 27, 27, -90, (float)(3.6 * used));
        var label = known ? used.ToString("0", CultureInfo.InvariantCulture) : "—";
        using var font = new Font("Segoe UI", label.Length >= 3 ? 11.5f : 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(known ? Color.White : accent);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(label, font, foreground, new RectangleF(2, 2, 28, 28), format);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { _ = DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
