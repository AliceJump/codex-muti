using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Point = System.Windows.Point;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CodexPool.Floating;

public sealed partial class PoolScene
{
    private readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer tipTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private TouchDevice? touchDevice;
    private ButtonBase? touchTarget;
    private ToolTip? touchTip;
    private Point touchStart, touchScreen;
    private double touchScrollStart;
    private bool touchMoved, touchHeld, touchCancelled, touchScroll;
    internal Action<Point>? ContextRequested;
    internal Action<Vector>? TouchDragRequested;
    internal Action? TouchDragCompleted;

    private void InitializeTouch()
    {
        // Own scene touch input so a swipe/hold never promotes to an account click.
        Stylus.SetIsPressAndHoldEnabled(this, false);
        PreviewTouchDown += (_, e) =>
        {
            e.Handled = true;
            if (touchDevice is not null) { touchCancelled = true; holdTimer.Stop(); return; }
            CloseTouchTip();
            var point = e.GetTouchPoint(this).Position;
            if (!SurfaceBounds.Contains(point)) return;
            touchDevice = e.TouchDevice; touchStart = point; touchScreen = PointToScreen(point);
            touchTarget = TouchTargetAt(point);
            touchScroll = viewport.Contains(point) && !actions.Any(a => a.Button == touchTarget);
            touchScrollStart = scroll;
            touchMoved = touchHeld = touchCancelled = false;
            if (!e.TouchDevice.Capture(this)) { CancelTouch(); return; }
            holdTimer.Start();
        };
        PreviewTouchMove += (_, e) =>
        {
            e.Handled = true;
            if (e.TouchDevice != touchDevice || touchCancelled || touchHeld) return;
            var point = e.GetTouchPoint(this).Position;
            var screen = PointToScreen(point);
            var delta = point - touchStart;
            var screenDelta = screen - touchScreen;
            if (!touchMoved && delta.Length < 8) return;
            if (!touchMoved) { touchMoved = true; holdTimer.Stop(); }
            if (touchScroll) { SetScroll(touchScrollStart - delta.Y); Present(); }
            else if (touchTarget is null)
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                TouchDragRequested?.Invoke(new Vector(screenDelta.X / dpi.DpiScaleX, screenDelta.Y / dpi.DpiScaleY));
            }
            touchScreen = screen;
        };
        PreviewTouchUp += (_, e) =>
        {
            e.Handled = true;
            if (e.TouchDevice != touchDevice) return;
            var target = touchTarget;
            var point = e.GetTouchPoint(this).Position;
            var tap = !touchCancelled && !touchMoved && !touchHeld && target is { IsEnabled: true }
                && (point - touchStart).Length < 8 && TouchTargetAt(point) == target;
            var dragged = touchMoved && !touchScroll && target is null;
            CancelTouch();
            if (dragged) TouchDragCompleted?.Invoke();
            if (!tap) return;
            target!.Focus();
            if (target is ToggleButton toggle) toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, toggle.IsChecked != true);
            else target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        };
        LostTouchCapture += (_, e) => { if (e.TouchDevice == touchDevice) CancelTouch(); };
        holdTimer.Tick += (_, _) =>
        {
            holdTimer.Stop();
            if (touchDevice is null || touchCancelled || touchMoved) return;
            touchHeld = true;
            if (touchTarget?.ToolTip is ToolTip tip)
            {
                touchTip = tip; tip.IsOpen = true;
            }
            else
            {
                var point = touchStart;
                CancelTouch(); ContextRequested?.Invoke(point);
            }
        };
        tipTimer.Tick += (_, _) => CloseTouchTip();
        // Windows may also emit compatibility mouse messages for WM_POINTER
        // touch input. They can have no StylusDevice, so inspect the native
        // touch signature rather than letting them click/drag a second time.
        PreviewMouseDown += (_, e) => { if (IsTouchMouseMessage()) e.Handled = true; else CloseTouchTip(); };
        PreviewMouseUp += (_, e) => { if (IsTouchMouseMessage()) e.Handled = true; };
        MouseRightButtonUp += (_, e) => { ContextRequested?.Invoke(e.GetPosition(this)); e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseTouchTip();
            if (e.Key != Key.Apps && !(e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) return;
            ContextRequested?.Invoke(new Point(SurfaceBounds.Left + 20, SurfaceBounds.Top + 20)); e.Handled = true;
        };
        Unloaded += (_, _) => { CancelTouch(); CloseTouchTip(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CancelTouch(); CloseTouchTip(); } };
    }

    private ButtonBase? TouchTargetAt(Point point)
    {
        foreach (var (button, _) in actions)
            if (button.IsVisible && button.IsHitTestVisible && new Rect(button.TranslatePoint(new Point(), this), button.RenderSize).Contains(point)) return button;
        // Disabled account cards still expose their status on a long press.
        if (viewport.Contains(point))
            foreach (var card in cards.Values)
                if (new Rect(card.Hit.TranslatePoint(new Point(), this), card.Hit.RenderSize).Contains(point)) return card.Hit;
        return null;
    }

    private void CancelTouch()
    {
        holdTimer.Stop();
        var device = touchDevice; touchDevice = null; touchTarget = null;
        if (device?.Captured == this) device.Capture(null);
        if (touchTip is not null && !tipTimer.IsEnabled) tipTimer.Start();
    }

    private void CloseTouchTip()
    {
        tipTimer.Stop();
        if (touchTip is not null) touchTip.IsOpen = false;
        touchTip = null;
    }

    internal static bool IsTouchMouseMessage() => IsTouchMouseMessage(GetMessageExtraInfo());
    internal static bool IsTouchMouseMessage(nint extraInfo) => ((long)extraInfo & 0xffffff80L) == 0xff515780L;
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
}
