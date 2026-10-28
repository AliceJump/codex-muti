using CodexPool.Floating;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class TouchTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }

    internal static void Run(Action<string, Action> run)
    {
        run("Touch tap invokes once, hold shows disabled account details and swipe never activates", () =>
        {
            var window = new FloatingWindow(false) { ShowActivated = false };
            try
            {
                window.Show(); window.Preview(SceneTests.State(count: 3), 1); Pump(1200);
                var scene = (PoolScene)window.FindName("Scene");
                var activated = new List<string>(); scene.ActivateAccount = activated.Add;
                var card = scene.AccountTargets["a1"];
                var point = card.TranslatePoint(new Point(20, 20), scene);
                using (var touch = new TestTouch(scene, point)) touch.Up();
                Check(activated.SequenceEqual(new[] { "a1" }));
                var disabled = scene.AccountTargets["a2"];
                point = disabled.TranslatePoint(new Point(20, 20), scene);
                using (var touch = new TestTouch(scene, point))
                {
                    Pump(680);
                    Check(((ToolTip)disabled.ToolTip).IsOpen);
                    touch.Up();
                }
                Check(activated.Count == 1 && ((ToolTip)disabled.ToolTip).IsOpen);
                window.Hide(); Check(!((ToolTip)disabled.ToolTip).IsOpen);
                window.Show(); window.Preview(SceneTests.State(count: 12), 0); Pump(1200);
                point = scene.AccountTargets["a0"].TranslatePoint(new Point(20, 50), scene);
                using (var touch = new TestTouch(scene, point))
                {
                    touch.Move(point - new Vector(0, 90)); touch.Up();
                }
                Check(scene.ScrollOffset >= 89 && activated.Count == 1);
            }
            finally { window.Close(); }
        });
        run("Touch cancellation, multiple fingers and pin toggle preserve action boundaries", () =>
        {
            var window = new FloatingWindow(false) { ShowActivated = false };
            try
            {
                window.Show(); window.Preview(SceneTests.State(), 1); Pump(1200);
                var scene = (PoolScene)window.FindName("Scene");
                var count = 0; scene.ActivateAccount = _ => count++;
                var point = scene.AccountTargets["a1"].TranslatePoint(new Point(20, 20), scene);
                using (var first = new TestTouch(scene, point))
                using (var second = new TestTouch(scene, point + new Vector(20, 0)))
                { second.Up(); first.Up(); }
                Check(count == 0);
                using (var touch = new TestTouch(scene, point)) { touch.Capture(null); touch.Up(); }
                Check(count == 0);
                var pin = scene.PinTarget;
                point = pin.TranslatePoint(new Point(10, 10), scene);
                using (var touch = new TestTouch(scene, point)) touch.Up();
                Check(window.Pinned && pin.IsChecked == true);
                Vector? drag = null; var completed = 0;
                scene.TouchDragRequested = delta => drag = delta;
                scene.TouchDragCompleted = () => completed++;
                var background = new Point(scene.SurfaceBounds.Left + 20, scene.SurfaceBounds.Top + 18);
                using (var touch = new TestTouch(scene, background)) { touch.Move(background + new Vector(20, 10)); touch.Up(); }
                Check(drag == new Vector(20, 10) && completed == 1 && count == 0);
                using (var touch = new TestTouch(scene, point)) touch.Up();
                Check(!window.Pinned && pin.IsChecked == false);
            }
            finally { window.Close(); }
        });
        run("Touch hold on scene background opens context action once and release does not click", () =>
        {
            var window = new FloatingWindow(false) { ShowActivated = false };
            try
            {
                window.Show(); window.Preview(SceneTests.State(), 1); Pump(1200);
                var scene = (PoolScene)window.FindName("Scene");
                var count = 0; scene.ContextRequested = _ => count++;
                var point = new Point(scene.SurfaceBounds.Left + 20, scene.SurfaceBounds.Top + 18);
                using (var touch = new TestTouch(scene, point)) { Pump(680); touch.Up(); }
                Check(count == 1);
                scene.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                Check(count == 2);
            }
            finally { window.Close(); }
        });
        run("Touch menu keeps tapped submenu open after mouse leave and closes before invoking", () =>
        {
            var invoked = false; var closed = false;
            var root = new GlassTrayMenu(new GlassMenuItem[] {
                new("选择账户", Children: new[] { new GlassMenuItem("备用账户", () => { Check(closed); invoked = true; }) }),
                new("禁用项", Children: new[] { new GlassMenuItem("不可调用", () => throw new InvalidOperationException()) }, Enabled: false)
            }, () => closed = true, touchMode: true);
            try
            {
                var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                root.ShowNear(new(work.Left + 400, work.Top + 500), null);
                var list = (StackPanel)((Border)root.Content).Child;
                var button = (Button)list.Children[0];
                Check(button.ActualHeight >= 40);
                // Native Button owns the tap; these routed events cover the menu's
                // touch-navigation policy independently of physical touch hardware.
                using (var touch = new TestTouch(button, new Point(15, 15))) touch.Up();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var child = root.Submenu!; Check(child.IsVisible);
                button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                Pump(300); Check(root.Submenu == child && child.IsVisible);
                var disabled = (Button)list.Children[1];
                disabled.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                Check(root.Submenu == child);
                var choice = (Button)((StackPanel)((Border)child.Content).Child).Children[0];
                choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(closed && invoked && !child.IsVisible && !root.IsVisible);
            }
            finally { root.Close(); }
        });
        run("Info tips wrap long text and follow light, dark and high contrast palettes", () =>
        {
            var window = new FloatingWindow(false) { ShowActivated = false };
            try
            {
                window.Show(); window.Preview(SceneTests.State(), 1); Pump(1200);
                var scene = (PoolScene)window.FindName("Scene");
                var target = scene.AccountTargets["a0"];
                var tip = (ToolTip)target.ToolTip;
                var output = System.IO.Path.GetFullPath(".research/touch-info-tips"); System.IO.Directory.CreateDirectory(output);
                foreach (var (name, light, contrast) in new[] { ("light", true, false), ("dark", false, false), ("contrast", false, true) })
                {
                    scene.SetTheme(light, contrast); tip.IsOpen = true; Pump(60); tip.UpdateLayout();
                    Check(tip.ActualWidth <= 366 && tip.ActualHeight > 100);
                    Check(tip.Background != tip.Foreground);
                    var color = ((SolidColorBrush)tip.Background).Color;
                    Check(color == (contrast ? SystemColors.InfoColor : light ? Color.FromRgb(247, 249, 253) : Color.FromRgb(30, 39, 53)));
                    var border = (Border)VisualTreeHelper.GetChild(tip, 0);
                    Check(border.CornerRadius.TopLeft == 9 && border.Padding == new Thickness(12, 9, 12, 9) && ((TextBlock)border.Child).TextWrapping == TextWrapping.Wrap);
                    var dpi = VisualTreeHelper.GetDpi(tip);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(tip.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(tip); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(output, name + ".png")); encoder.Save(file);
                    tip.IsOpen = false;
                }
            }
            finally { window.Close(); }
        });
    }

    // Reports WPF touch input through its input manager, including routing and
    // capture. This is synthetic UI coverage, not a physical touchscreen claim.
    private sealed class TestTouch : TouchDevice, IDisposable
    {
        private static int nextId = 100;
        private readonly UIElement root;
        private Point position;
        private TouchAction action;
        internal TestTouch(UIElement root, Point position) : base(nextId++)
        {
            this.root = root; this.position = position;
            SetActiveSource(PresentationSource.FromVisual(root)); Activate(); action = TouchAction.Down; ReportDown();
        }
        internal void Move(Point point) { position = point; action = TouchAction.Move; ReportMove(); }
        internal void Up() { if (!IsActive) return; action = TouchAction.Up; ReportUp(); Deactivate(); }
        public void Dispose() => Up();
        public override TouchPoint GetTouchPoint(IInputElement relativeTo)
        {
            var point = relativeTo is UIElement element ? root.TranslatePoint(position, element) : position;
            return new TouchPoint(this, point, new Rect(point, new Size(1, 1)), action);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => new() { GetTouchPoint(relativeTo) };
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
