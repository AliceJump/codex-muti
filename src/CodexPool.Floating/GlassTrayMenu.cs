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
    private readonly Action? onClosed;

    public GlassTrayMenu(IReadOnlyList<(string Label, Action Invoke)> items, Action? onClosed = null)
    {
        this.onClosed = onClosed;
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
            button.Click += (_, _) => { capture.Invoke(); Close(); };
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
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => onClosed?.Invoke();
    }

    public void ShowNear(System.Drawing.Point cursor, System.Drawing.Rectangle? trayBounds)
    {
        ApplyTheme();
        Show();
        UpdateLayout();
        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);
        var screen = Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var anchor = trayBounds is { Width: > 0, Height: > 0 } b
            ? b
            : new System.Drawing.Rectangle(cursor.X - 8, cursor.Y - 8, 16, 16);
        var point = FlyoutPlacement.Above(work, anchor, width, height, 8);
        Left = point.X;
        Top = point.Y;
        Activate();
    }

    private void ApplyTheme()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value == 1;
        var highContrast = SystemParameters.HighContrast;
        Resources["ForegroundBrush"] = highContrast ? SystemColors.WindowTextBrush : new Brush(light ? MediaColor.FromRgb(26, 38, 53) : MediaColor.FromRgb(237, 242, 249));
        Resources["SurfaceBrush"] = highContrast ? SystemColors.WindowBrush : new Brush(light ? MediaColor.FromArgb(backdrop ? (byte)190 : (byte)255, 245, 248, 253) : MediaColor.FromArgb(backdrop ? (byte)185 : (byte)255, 24, 30, 39));
        Resources["EdgeBrush"] = new Brush(light ? MediaColor.FromArgb(70, 130, 145, 165) : MediaColor.FromArgb(70, 170, 187, 210));
        Foreground = (Brush)Resources["ForegroundBrush"];
        var dark = light ? 0 : 1;
        if (handle != 0) _ = DwmSetWindowAttribute(handle, 20, ref dark, 4);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}