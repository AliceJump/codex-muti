using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using Brush = System.Windows.Media.SolidColorBrush;
using SystemColors = System.Windows.SystemColors;
using Button = System.Windows.Controls.Button;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaCursors = System.Windows.Input.Cursors;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace CodexPool.Floating;

/// <summary>
/// 托盘右键菜单：WPF 玻璃弹出层，视觉与 FloatingWindow 一致；保留原有 6 项语义。
/// </summary>
internal sealed class GlassTrayMenu : Window
{
    private nint handle;
    private bool backdrop;
    private bool closing;
    private readonly Action? onClosed;
    private readonly int theme;
    private System.Drawing.Point placementCursor;
    private System.Drawing.Rectangle? placementAnchor;
    private bool positioning;

    public GlassTrayMenu(IReadOnlyList<(string Label, Action Invoke)> items, Action? onClosed = null, int theme = 0)
    {
        this.onClosed = onClosed;
        this.theme = theme;
        Title = "Codex Pool · 托盘菜单";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = MediaBrushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new MediaFontFamily("Segoe UI Variable, Segoe UI");
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

        var list = new StackPanel { Margin = new Thickness(6) };
        foreach (var item in items)
        {
            var capture = item;
            var button = new Button
            {
                Content = capture.Label,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 1, 0, 1),
                MinWidth = 196,
                BorderThickness = new Thickness(0),
                Background = MediaBrushes.Transparent,
                Cursor = MediaCursors.Hand
            };
            // Actions may activate another window or shut down the application.
            // Release this popup before handing control to them.
            button.Click += (_, _) => { Close(); capture.Invoke(); };
            list.Children.Add(button);
        }

        var surface = new Border
        {
            Child = list,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");
        Content = surface;
        Resources["ForegroundBrush"] = new Brush(MediaColor.FromRgb(237, 242, 249));
        Resources["SurfaceBrush"] = new Brush(MediaColor.FromArgb(200, 24, 30, 39));
        Resources["EdgeBrush"] = new Brush(MediaColor.FromArgb(70, 170, 187, 210));
        Foreground = (Brush)Resources["ForegroundBrush"];

        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(PositionBeforeDisplay);
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var acrylic = 3;
            backdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && DwmSetWindowAttribute(handle, 38, ref acrylic, 4) == 0;
            var corners = 2;
            _ = DwmSetWindowAttribute(handle, 33, ref corners, 4);
            ApplyTheme();
        };
        Closing += (_, _) => { closing = true; positioning = false; };
        Deactivated += (_, _) => { if (!closing) Close(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => onClosed?.Invoke();
    }

    public void ShowNear(System.Drawing.Point cursor, System.Drawing.Rectangle? trayBounds)
    {
        placementCursor = cursor;
        placementAnchor = trayBounds;
        ApplyTheme();
        ShowActivated = false;
        _ = new WindowInteropHelper(this).EnsureHandle();
        // WinForms supplies physical pixels; WPF Left/Top and ActualWidth/Height
        // are device-independent units. Move the HWND to the target monitor first
        // so WPF has its DPI before measuring and placing the popup.
        if (!SetWindowPos(handle, 0, cursor.X, cursor.Y, 0, 0, 0x0001 | 0x0004 | 0x0010)) { Close(); return; }
        // Window.Opacity only covers WPF content, not the native DWM backdrop.
        // Measure and position the hidden HWND before its first visible frame.
        var content = (UIElement)Content;
        content.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Width * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height * dpi.DpiScaleY));
        var point = Placement(width, height);
        if (!SetWindowPos(handle, 0, point.X, point.Y, width, height, 0x0004 | 0x0010)) { Close(); return; }
        positioning = true;
        Show();
        UpdateLayout();
        Activate();
    }

    private System.Drawing.Point Placement(int width, int height)
    {
        var screen = Forms.Screen.FromPoint(placementCursor);
        var work = screen.WorkingArea;
        var anchor = placementAnchor is { Width: > 0, Height: > 0 } b
            ? b
            : new System.Drawing.Rectangle(placementCursor.X - 8, placementCursor.Y - 8, 16, 16);
        var gap = (int)Math.Round(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        return FlyoutPlacement.Above(work, anchor, width, height, gap);
    }

    private nint PositionBeforeDisplay(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0046 || !positioning || closing || lParam == 0) return 0; // WM_WINDOWPOSCHANGING
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        var width = position.Width; var height = position.Height;
        if ((position.Flags & 0x0001) != 0 && GetWindowRect(window, out var rect))
        { width = rect.Right - rect.Left; height = rect.Bottom - rect.Top; }
        // SizeToContent may adjust native dimensions during Show(). Anchor that
        // resize in the same native transaction, before Windows paints the backdrop.
        if (width > 0 && height > 0)
        {
            var point = Placement(width, height);
            position.X = point.X; position.Y = point.Y; position.Flags &= ~0x0002u; // clear SWP_NOMOVE
            Marshal.StructureToPtr(position, lParam, false);
        }
        return 0;
    }

    private void ApplyTheme()
    {
        var light = theme == 1 || theme == 0 && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value == 1;
        var highContrast = SystemParameters.HighContrast;
        Resources["ForegroundBrush"] = highContrast ? SystemColors.WindowTextBrush : new Brush(light ? MediaColor.FromRgb(26, 38, 53) : MediaColor.FromRgb(237, 242, 249));
        Resources["SurfaceBrush"] = highContrast ? SystemColors.WindowBrush : new Brush(light ? MediaColor.FromArgb(backdrop ? (byte)190 : (byte)255, 245, 248, 253) : MediaColor.FromArgb(backdrop ? (byte)185 : (byte)255, 24, 30, 39));
        Resources["EdgeBrush"] = new Brush(light ? MediaColor.FromArgb(70, 130, 145, 165) : MediaColor.FromArgb(70, 170, 187, 210));
        Foreground = (Brush)Resources["ForegroundBrush"];
        var dark = light ? 0 : 1;
        if (handle != 0) _ = DwmSetWindowAttribute(handle, 20, ref dark, 4);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public nint Window, After; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
