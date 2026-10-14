using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexPool.Floating;

internal static class ShapeMotion
{
    internal static IDisposable Apply(Window host, FrameworkElement surface, ClockGroup clock,
        Func<System.Drawing.Size, System.Drawing.Point>? placement = null)
    {
        // Only the surface owns animation clocks. Animating Window dimensions
        // as well would make WPF submit extra, partially updated HWND resizes.
        var width = (AnimationClock)clock.Children[0];
        var height = (AnimationClock)clock.Children[1];
        surface.ApplyAnimationClock(FrameworkElement.WidthProperty, width);
        surface.ApplyAnimationClock(FrameworkElement.HeightProperty, height);
        return new NativeFrames(host, surface, clock, placement);
    }
    private sealed class NativeFrames : IDisposable
    {
        private readonly Window host;
        private readonly FrameworkElement surface;
        private readonly Func<System.Drawing.Size, System.Drawing.Point>? placement;
        private readonly ClockGroup clock;
        private bool disposed;
        internal NativeFrames(Window host, FrameworkElement surface, ClockGroup clock,
            Func<System.Drawing.Size, System.Drawing.Point>? placement)
        {
            this.host = host; this.surface = surface; this.clock = clock; this.placement = placement;
            CompositionTarget.Rendering += Render;
            clock.Completed += Completed;
            host.Closed += Closed;
        }
        private void Render(object? sender, EventArgs e)
        {
            if (disposed) return;
            var handle = new WindowInteropHelper(host).Handle;
            var width = surface.Width; var height = surface.Height;
            if (handle == 0 || !double.IsFinite(width) || !double.IsFinite(height)) return;
            var dpi = VisualTreeHelper.GetDpi(host);
            var pixelsX = (int)Math.Ceiling(width * dpi.DpiScaleX);
            var pixelsY = (int)Math.Ceiling(height * dpi.DpiScaleY);
            if (!GetWindowRect(handle, out var rect)) return;
            var point = placement?.Invoke(new(pixelsX, pixelsY)) ?? new System.Drawing.Point(rect.Left, rect.Top);
            // Size and anchor position are a single native transaction per frame.
            if (rect.Right - rect.Left != pixelsX || rect.Bottom - rect.Top != pixelsY || rect.Left != point.X || rect.Top != point.Y)
                _ = SetWindowPos(handle, 0, point.X, point.Y, pixelsX, pixelsY, 0x0004 | 0x0010);
        }
        private void Completed(object? sender, EventArgs e)
        {
            // Group completion can precede the children's last invalidation.
            host.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { Render(sender, e); Dispose(); });
        }
        private void Closed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CompositionTarget.Rendering -= Render;
            clock.Completed -= Completed;
            host.Closed -= Closed;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
