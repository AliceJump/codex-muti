using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CodexPool.Core;

namespace CodexPool.Floating;

internal static class QuotaTrayIcon
{
    // Native tray icons are tiny: number = remaining %; exhausted binding window draws an unmistakable X.
    public static Icon Create(double? remainingPercent, bool exhausted = false)
    {
        var state = new QuotaIconState(remainingPercent, exhausted);
        var known = state.Known;
        var remaining = state.Amount;
        var accent = Color.FromArgb(unchecked((int)state.AccentArgb));
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var background = new SolidBrush(Color.FromArgb(24, 33, 48));
        graphics.FillEllipse(background, 0, 0, 32, 32);
        using var track = new Pen(Color.FromArgb(70, 83, 100), 2.5f);
        graphics.DrawEllipse(track, 2.5f, 2.5f, 27, 27);
        if (exhausted)
        {
            using var cross = new Pen(accent, 3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(cross, 10f, 10f, 22f, 22f);
            graphics.DrawLine(cross, 22f, 10f, 10f, 22f);
        }
        else
        {
            using var stroke = new Pen(accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (known && remaining > 0) graphics.DrawArc(stroke, 2.5f, 2.5f, 27, 27, -90, (float)(3.6 * remaining));
            var label = state.Label;
            using var font = new Font("Segoe UI", label.Length >= 3 ? 11.5f : 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(known ? Color.White : accent);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(label, font, foreground, new RectangleF(2, 2, 28, 28), format);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { _ = DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
