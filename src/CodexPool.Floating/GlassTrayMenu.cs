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
using System.Windows.Threading;

namespace CodexPool.Floating;

internal sealed record GlassMenuItem(string Label, Action? Invoke = null, IReadOnlyList<GlassMenuItem>? Children = null, bool Enabled = true, bool IsChecked = false);

/// <summary>
/// 托盘右键菜单：WPF 玻璃弹出层，视觉与 FloatingWindow 一致。
/// </summary>
internal sealed class GlassTrayMenu : Window
{
    private nint handle;
    private bool backdrop;
    private bool closing;
    private readonly Action? onClosed;
    private readonly int theme;
    private readonly bool touchLayout;
    private bool touchNavigation;
    private System.Drawing.Point placementCursor;
    private System.Drawing.Rectangle? placementAnchor;
    private bool positioning;
    private bool beside;
    private GlassTrayMenu? parentMenu;
    private GlassTrayMenu? submenu;
    private Button? submenuOwner;
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    internal GlassTrayMenu? Submenu => submenu;

    public GlassTrayMenu(IReadOnlyList<(string Label, Action Invoke)> items, Action? onClosed = null, int theme = 0)
        : this(items.Select(x => new GlassMenuItem(x.Label, x.Invoke)).ToArray(), onClosed, theme) { }

    internal GlassTrayMenu(IReadOnlyList<GlassMenuItem> items, Action? onClosed = null, int theme = 0, bool? touchMode = null)
    {
        this.onClosed = onClosed;
        this.theme = theme;
        touchLayout = touchMode ?? GetSystemMetrics(95) > 0; // SM_MAXIMUMTOUCHES
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Title = "Codex Pool · 托盘菜单";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = MediaBrushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new MediaFontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 12;
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(8),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

        var list = new StackPanel { Margin = new Thickness(3) };
        foreach (var item in items)
        {
            var capture = item;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.Children.Add(new TextBlock { Text = capture.IsChecked ? "✓" : "", VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = capture.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var arrow = new TextBlock { Text = capture.Children?.Count > 0 ? "›" : "", FontSize = 16, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 2); row.Children.Add(arrow);
            var button = new Button
            {
                Content = row,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0),
                MinHeight = touchLayout ? 40 : 26,
                MinWidth = 172,
                MaxWidth = 244,
                IsEnabled = capture.Enabled,
                BorderThickness = new Thickness(0),
                Background = MediaBrushes.Transparent,
                Cursor = MediaCursors.Hand
            };
            if (capture.Children?.Count is not > 0) InfoTip.Set(button, capture.Label);
            // Actions may activate another window or shut down the application.
            // Release this popup before handing control to them.
            System.Windows.Automation.AutomationProperties.SetName(button, capture.Label);
            button.Click += (_, _) =>
            {
                if (!capture.Enabled) return;
                if (capture.Children?.Count > 0) { OpenSubmenu(button, capture.Children); return; }
                CloseGroup(); capture.Invoke?.Invoke();
            };
            button.MouseEnter += (_, e) =>
            {
                if (!capture.Enabled || e.StylusDevice is not null || RootMenu().touchNavigation) return;
                if (capture.Children?.Count > 0) OpenSubmenu(button, capture.Children);
                else ScheduleSubmenuClose();
            };
            button.MouseLeave += (_, _) => ScheduleSubmenuClose();
            list.Children.Add(button);
        }

        var surface = new Border
        {
            Child = items.Count > 8 ? new ScrollViewer { Content = list, MaxHeight = touchLayout ? 400 : 260, PanningMode = PanningMode.VerticalOnly, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : list,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1)
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");
        Content = surface;
        PreviewTouchDown += (_, _) =>
        {
            RootMenu().touchNavigation = true;
            for (var menu = RootMenu(); menu is not null; menu = menu.submenu) menu.hoverTimer.Stop();
        };
        PreviewMouseMove += (_, e) => { if (e.StylusDevice is null) RootMenu().touchNavigation = false; };
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
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (!RootMenu().touchNavigation && submenuOwner?.IsMouseOver != true && submenu?.IsMouseOver != true) CloseSubmenu();
        };
        MouseEnter += (_, _) => parentMenu?.hoverTimer.Stop();
        MouseLeave += (_, _) => parentMenu?.ScheduleSubmenuClose();
        Closing += (_, _) => { closing = true; positioning = false; hoverTimer.Stop(); CloseSubmenu(); };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var root = RootMenu();
            if (!root.closing && !root.GroupActive()) root.Close();
        });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => onClosed?.Invoke();
    }

    public void ShowNear(System.Drawing.Point cursor, System.Drawing.Rectangle? trayBounds, bool activate = true)
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
        if (activate) Activate();
    }

    private System.Drawing.Point Placement(int width, int height)
    {
        var screen = Forms.Screen.FromPoint(placementCursor);
        var work = screen.WorkingArea;
        var anchor = placementAnchor is { Width: > 0, Height: > 0 } b
            ? b
            : new System.Drawing.Rectangle(placementCursor.X - 8, placementCursor.Y - 8, 16, 16);
        var gap = (int)Math.Round(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (beside) return FlyoutPlacement.Beside(work, anchor, width, height, 0);
        return FlyoutPlacement.Above(work, anchor, width, height, gap);
    }

    private GlassTrayMenu RootMenu() => parentMenu?.RootMenu() ?? this;
    private bool GroupActive() => IsActive || handle != 0 && GetForegroundWindow() == handle || submenu?.GroupActive() == true;
    private void CloseGroup() => RootMenu().Close();
    private void ScheduleSubmenuClose() { hoverTimer.Stop(); if (submenu is not null && !RootMenu().touchNavigation) hoverTimer.Start(); }
    private void CloseSubmenu() { hoverTimer.Stop(); submenu?.Close(); submenu = null; submenuOwner = null; }
    private void OpenSubmenu(Button owner, IReadOnlyList<GlassMenuItem> children)
    {
        if (closing) return;
        hoverTimer.Stop();
        if (submenu is not null && submenuOwner == owner) return;
        CloseSubmenu();
        if (!GetWindowRect(handle, out var parentRect)) return;
        var top = owner.PointToScreen(new System.Windows.Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(owner);
        var anchor = new System.Drawing.Rectangle(parentRect.Left, (int)Math.Round(top.Y), parentRect.Right - parentRect.Left, (int)Math.Ceiling(owner.ActualHeight * dpi.DpiScaleY));
        GlassTrayMenu? child = null;
        child = new GlassTrayMenu(children, () => { if (ReferenceEquals(submenu, child)) { submenu = null; submenuOwner = null; } }, theme, touchLayout || RootMenu().touchNavigation)
            { Owner = this, parentMenu = this, beside = true };
        submenu = child; submenuOwner = owner;
        child.ShowNear(new System.Drawing.Point(anchor.Left + anchor.Width / 2, anchor.Top), anchor, activate: false);
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
        InfoTip.SetTheme(this, light, highContrast);
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
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
