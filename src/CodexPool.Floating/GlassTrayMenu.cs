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
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var acrylic = 3;
            backdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && DwmSetWindowAttribute(handle, 38, ref acrylic, 4) == 0;
            var corners = 2;
            _ = DwmSetWindowAttribute(handle, 33, ref corners, 4);
            ApplyTheme();
        };
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) => { if (!closing) Close(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => onClosed?.Invoke();
    }

    public void ShowNear(System.Drawing.Point cursor, System.Drawing.Rectangle? trayBounds)
    {
        ApplyTheme();
        Opacity = 0;
        Show();
        // WinForms supplies physical pixels; WPF Left/Top and ActualWidth/Height
        // are device-independent units. Move the HWND to the target monitor first
        // so WPF has its DPI before measuring and placing the popup.
        _ = SetWindowPos(handle, 0, cursor.X, cursor.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        UpdateLayout();
        if (!GetWindowRect(handle, out var rect)) { Close(); return; }
        var screen = Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var anchor = trayBounds is { Width: > 0, Height: > 0 } b
            ? b
            : new System.Drawing.Rectangle(cursor.X - 8, cursor.Y - 8, 16, 16);
        var gap = (int)Math.Round(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var point = FlyoutPlacement.Above(work, anchor, rect.Right - rect.Left, rect.Bottom - rect.Top, gap);
        _ = SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        Opacity = 1;
        Activate();
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
