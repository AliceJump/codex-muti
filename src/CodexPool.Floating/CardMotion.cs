using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace CodexPool.Floating;

internal sealed record CardPose(Rect Bounds, Color Fill, Color Stroke, double Radius, double Opacity);
internal sealed record CardRestore(Border Card, System.Windows.Media.Brush Background, System.Windows.Media.Brush Stroke);

internal static class CardMotion
{
    internal static Dictionary<Border, CardPose> Capture(FrameworkElement root, IEnumerable<Border> cards) =>
        cards.Where(x => root.IsAncestorOf(x) && x.ActualWidth > 0 && x.ActualHeight > 0).ToDictionary(x => x,
            x => new CardPose(x.TransformToAncestor(root).TransformBounds(new Rect(x.RenderSize)),
                (x.Background as SolidColorBrush)?.Color ?? Colors.Transparent,
                (x.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent, x.CornerRadius.TopLeft, x.Opacity));
    internal static void Play(FrameworkElement root, Canvas overlay, Dictionary<Border, CardPose> previous, Duration duration)
    {
        var saved = new List<CardRestore>(); overlay.Tag = saved;
        var origin = overlay.TransformToAncestor(root).Transform(new Point());
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Move(double from, double to) => new(from, to, duration) { EasingFunction = ease };
        foreach (var (card, old) in previous)
        {
            if (!root.IsAncestorOf(card) || card.ActualWidth <= 0 || card.ActualHeight <= 0) continue;
            var target = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
            var fill = new SolidColorBrush((card.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
            var stroke = new SolidColorBrush((card.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent);
            var shape = new System.Windows.Shapes.Rectangle
            {
                Width = target.Width, Height = target.Height, Fill = fill, Stroke = stroke,
                StrokeThickness = card.BorderThickness.Left, RadiusX = card.CornerRadius.TopLeft, RadiusY = card.CornerRadius.TopLeft, Opacity = card.Opacity
            };
            Canvas.SetLeft(shape, target.Left - origin.X); Canvas.SetTop(shape, target.Top - origin.Y);
            overlay.Children.Add(shape);
            saved.Add(new(card, card.Background, card.BorderBrush));
            card.Background = Brushes.Transparent; card.BorderBrush = Brushes.Transparent;
            shape.BeginAnimation(Canvas.LeftProperty, Move(old.Bounds.Left - origin.X, target.Left - origin.X));
            shape.BeginAnimation(Canvas.TopProperty, Move(old.Bounds.Top - origin.Y, target.Top - origin.Y));
            shape.BeginAnimation(FrameworkElement.WidthProperty, Move(old.Bounds.Width, target.Width));
            shape.BeginAnimation(FrameworkElement.HeightProperty, Move(old.Bounds.Height, target.Height));
            shape.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusXProperty, Move(old.Radius, shape.RadiusX));
            shape.BeginAnimation(System.Windows.Shapes.Rectangle.RadiusYProperty, Move(old.Radius, shape.RadiusY));
            shape.BeginAnimation(UIElement.OpacityProperty, Move(old.Opacity, card.Opacity));
            fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(old.Fill, fill.Color, duration) { EasingFunction = ease });
            stroke.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(old.Stroke, stroke.Color, duration) { EasingFunction = ease });
        }
    }
    internal static void Clear(Canvas overlay)
    {
        if (overlay.Tag is List<CardRestore> saved)
            foreach (var item in saved) { item.Card.Background = item.Background; item.Card.BorderBrush = item.Stroke; }
        overlay.Tag = null; overlay.Children.Clear();
    }
}
