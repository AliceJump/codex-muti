using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexPool.Floating;

internal static class ShapeMotion
{
    internal static IDisposable Apply(Window host, FrameworkElement surface, ClockGroup clock)
    {
        // One clock drives both the native frame and visible surface. A fixed
        // target content layout inside the surface preserves the existing morph.
        var width = (AnimationClock)clock.Children[0];
        var height = (AnimationClock)clock.Children[1];
        host.ApplyAnimationClock(FrameworkElement.WidthProperty, width);
        host.ApplyAnimationClock(FrameworkElement.HeightProperty, height);
        surface.ApplyAnimationClock(FrameworkElement.WidthProperty, width);
        surface.ApplyAnimationClock(FrameworkElement.HeightProperty, height);
        return new NativeFrames(host, clock);
    }
    private sealed class NativeFrames : IDisposable
    {
        private readonly Window host;
        private readonly ClockGroup clock;
        private bool disposed;
        internal NativeFrames(Window host, ClockGroup clock)
        {
            this.host = host; this.clock = clock;
            CompositionTarget.Rendering += Render;
            clock.Completed += Completed;
            host.Closed += Closed;
        }
        private void Render(object? sender, EventArgs e)
        {
            if (disposed) return;
            var handle = new WindowInteropHelper(host).Handle;
            var width = host.Width; var height = host.Height;
            if (handle == 0 || !double.IsFinite(width) || !double.IsFinite(height)) return;
            var dpi = VisualTreeHelper.GetDpi(host);
            var pixelsX = (int)Math.Ceiling(width * dpi.DpiScaleX);
            var pixelsY = (int)Math.Ceiling(height * dpi.DpiScaleY);
            // Commit both dimensions after the animation/layout phase; separate
            // Window property invalidations can submit one old native dimension.
            if (GetWindowRect(handle, out var rect) && (rect.Right - rect.Left != pixelsX || rect.Bottom - rect.Top != pixelsY))
                _ = SetWindowPos(handle, 0, 0, 0, pixelsX, pixelsY, 0x0002 | 0x0004 | 0x0010);
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
