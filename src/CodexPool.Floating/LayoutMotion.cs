using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CodexPool.Floating;

internal sealed record ElementPose(Rect Bounds, Color? Color, BitmapSource? Snapshot);

internal static class LayoutMotion
{
    internal static Dictionary<FrameworkElement, ElementPose> Capture(FrameworkElement root, IEnumerable<FrameworkElement> elements)
    {
        var result = new Dictionary<FrameworkElement, ElementPose>();
        foreach (var element in elements)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0 || !root.IsAncestorOf(element)) continue;
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            var color = element is TextBlock text && text.Foreground is SolidColorBrush brush ? brush.Color : (Color?)null;
            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(element); bitmap.Freeze();
            result[element] = new(bounds, color, bitmap);
        }
        return result;
    }
    internal static void Play(FrameworkElement root, Canvas overlay, Dictionary<FrameworkElement, ElementPose> previous, Duration duration, IEnumerable<FrameworkElement> current)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Move(double from, double to) => new(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        foreach (var (element, pose) in previous)
        {
            if (element.Visibility == Visibility.Visible && root.IsAncestorOf(element) && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                var target = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                var scale = new ScaleTransform(1, 1);
                var offset = new TranslateTransform();
                var transform = new TransformGroup(); transform.Children.Add(scale); transform.Children.Add(offset);
                element.RenderTransform = transform;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Move(pose.Bounds.Width / target.Width, 1));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Move(pose.Bounds.Height / target.Height, 1));
                offset.BeginAnimation(TranslateTransform.XProperty, Move(pose.Bounds.Left - target.Left, 0));
                offset.BeginAnimation(TranslateTransform.YProperty, Move(pose.Bounds.Top - target.Top, 0));
                if (pose.Color is { } color && element is TextBlock text && text.Foreground is SolidColorBrush foreground)
                {
                    var next = foreground.CloneCurrentValue(); text.Foreground = next;
                    next.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, next.Color, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                }
            }
            else if (pose.Snapshot is { } bitmap)
            {
                // Details absent from the capsule shrink away instead of vanishing on the first frame.
                var ghost = new Image { Source = bitmap, Width = pose.Bounds.Width, Height = pose.Bounds.Height, RenderTransformOrigin = new Point(.5, .5) };
                Canvas.SetLeft(ghost, pose.Bounds.Left); Canvas.SetTop(ghost, pose.Bounds.Top);
                overlay.Children.Add(ghost);
                var scale = new ScaleTransform(1, 1); ghost.RenderTransform = scale;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Move(1, .8));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Move(1, .2));
                ghost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
            }
        }
        foreach (var element in current.Where(x => !previous.ContainsKey(x) && root.IsAncestorOf(x) && x.Visibility == Visibility.Visible))
            element.BeginAnimation(UIElement.OpacityProperty, Move(0, element.Opacity));
    }
}
