using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using SystemColors = System.Windows.SystemColors;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace CodexPool.Floating;

// One scene owns all pixels. Native controls above it supply keyboard, focus,
// tooltips and automation, but never lay out the text or scale the native HWND.
public sealed class PoolScene : Canvas
{
    private const double TokenSectionHeight = 52;
    private static readonly ConcurrentDictionary<string, SolidColorBrush> brushes = new();
    private static readonly ConcurrentDictionary<(string, double), Pen> pens = new();
    private static readonly LinearGradientBrush lightBackground = Gradient("#F0F6FAFF", "#E7E3ECF8");
    private static readonly LinearGradientBrush darkBackground = Gradient("#EE1B2331", "#E4101621");
    private sealed class Card(PoolAccountView model, MorphSpring pose, Button hit)
    { internal PoolAccountView Model = model; internal MorphSpring Pose = pose; internal Button Hit = hit; }
    private readonly SceneDrawing drawing;
    private readonly Canvas hits = new();
    private readonly Dictionary<string, Card> cards = new();
    private readonly Dictionary<string, FormattedText> text = new();
    private readonly Dictionary<double, Geometry> rings = new();
    private readonly List<(ButtonBase Button, string Action)> actions = [];
    private readonly Stopwatch watch = new();
    private double last;
    private TimeSpan renderingTime;
    private readonly MorphSpring scrollMotion = new(0);
    private readonly MorphSpring hostOffset = new(0, 0);
    private readonly MorphSpring entrance = new(1);
    private readonly TranslateTransform entranceTranslation = new();
    private double scroll => scrollMotion.Value[0];
    private double requestedScroll;
    private double contentHeight, viewportHeight;
    private Rect viewport;
    private bool light, highContrast;
    private int mode;
    private bool busy;
    private bool settingPin;
    private bool entrancePending;
    private string status = "连接中", notice = "";
    private bool automatic;
    private int runningRequests;
    private PoolAccountView[] accounts = [];
    internal readonly MorphSpring Shape = new(336, 140, 26, 1, 1);
    internal bool Animating { get; private set; }
    internal bool AtTop { get; set; }
    internal double HorizontalAnchor { get; set; } = .5;
    internal Action<string>? ActivateAccount;
    internal Action<string>? InvokeAction;
    internal Action? DragRequested;
    internal Action? BoundsChanged;
    internal IReadOnlyList<PoolAccountView> Accounts => accounts;
    internal int Mode => mode;
    internal double ScrollOffset => scroll;
    internal double EntranceProgress => entrance.Value[0];
    internal Rect SurfaceBounds
    {
        get { var bounds = BoundsFor(Shape.Value); bounds.Offset(hostOffset.Value[0], hostOffset.Value[1]); return bounds; }
    }
    internal Rect TargetSurfaceBounds => BoundsFor(Shape.Target);
    private Rect BoundsFor(double[] shape) => new(Math.Clamp(ActualWidth * HorizontalAnchor - shape[0] / 2, 10,
        Math.Max(10, ActualWidth - shape[0] - 10)), AtTop ? 10 : ActualHeight - 10 - shape[1], shape[0], shape[1]);
    internal IReadOnlyDictionary<string, Button> AccountTargets => cards.ToDictionary(x => x.Key, x => x.Value.Hit);
    internal ToggleButton PinTarget => (ToggleButton)actions.First(a => a.Action == "pin").Button;
    public PoolScene()
    {
        Focusable = false; ClipToBounds = false; RenderTransform = entranceTranslation;
        drawing = new(this) { IsHitTestVisible = false }; Children.Add(drawing); Children.Add(hits);
        foreach (var (label, action) in new[] { ("◫", "mode"), ("◐", "theme"), ("\uE718", "pin"), ("×", "close"), ("Web 面板 ↗", "web"), ("刷新额度", "refresh") })
        {
            ButtonBase b = action == "pin" ? new ToggleButton { FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets") } : new Button();
            b.Content = label; b.Height = 24; b.Width = action is "mode" or "theme" or "pin" or "close" ? 26 : 84;
            b.Padding = new(2); b.FontSize = 10; b.BorderThickness = new(0); b.Background = Brushes.Transparent; b.Cursor = Cursors.Hand;
            b.ToolTip = action switch { "mode" => "完整 / 紧凑 / 胶囊（Ctrl+M）", "theme" => "自动 / 浅色 / 深色", "close" => "关闭或隐藏窗口", _ => label };
            AutomationProperties.SetName(b, b.ToolTip.ToString());
            if (b is ToggleButton pin)
            {
                pin.Checked += (_, _) => { if (!settingPin) InvokeAction?.Invoke("pin"); };
                pin.Unchecked += (_, _) => { if (!settingPin) InvokeAction?.Invoke("pin"); };
            }
            else b.Click += (_, _) => InvokeAction?.Invoke(action);
            actions.Add((b, action)); Children.Add(b);
        }
        SizeChanged += (_, _) => { drawing.Width = ActualWidth; drawing.Height = ActualHeight; Present(); };
        MouseWheel += (_, e) => { SetScroll(scroll - e.Delta / 3d); Present(); e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            var next = e.Key switch { Key.PageDown => scroll + viewportHeight * .8, Key.PageUp => scroll - viewportHeight * .8,
                Key.Home => 0, Key.End => contentHeight, _ => scroll };
            if (next == scroll) return;
            SetScroll(next); Present(); e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) => { if (SurfaceBounds.Contains(e.GetPosition(this)) && e.OriginalSource == this) DragRequested?.Invoke(); };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { Stop(); SnapMotion(); Present(); } };
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => SurfaceBounds.Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;
    internal void SetTheme(bool useLight, bool contrast)
    {
        light = useLight; highContrast = contrast; text.Clear();
        if (highContrast || !SystemParameters.ClientAreaAnimation) { Stop(); SnapMotion(); }
        var fg = Foreground;
        foreach (var (b, _) in actions) b.Foreground = fg;
        Present();
    }
    internal void SetPinned(bool value)
    {
        var pin = PinTarget;
        settingPin = true;
        try { pin.IsChecked = value; } finally { settingPin = false; }
        pin.ToolTip = value ? "取消固定，恢复托盘弹窗" : "固定面板，保持显示并允许拖动";
        AutomationProperties.SetName(pin, pin.ToolTip.ToString());
        Present();
    }
    internal void SetNotice(string value) { notice = value; UpdateTip(); Present(); }
    internal void SetBusy(bool value) { busy = value; foreach (var c in cards.Values) c.Hit.IsEnabled = !busy && c.Model.Selectable; Present(); }
    internal void SetState(PoolAccountView[] value, int newMode, bool online, bool autoSwitch, bool animate, int requests = 0)
    {
        accounts = value; mode = newMode; automatic = autoSwitch; runningRequests = requests; UpdateTip();
        status = online ? "● 服务在线 · Web 已启用" : "服务离线 · 正在重新连接";
        var visible = value.Where(a => a.Enabled || a.Active).ToArray();
        foreach (var removed in cards.Keys.Where(id => !visible.Any(a => a.Id == id)).ToArray()) { hits.Children.Remove(cards[removed].Hit); cards.Remove(removed); }
        var budgetWidth = ActualWidth > 0 ? ActualWidth - 20 : 580;
        var capsuleWidth = visible.Sum(model => ChipWidth(model) + 8) + 8;
        var width = Math.Min(budgetWidth, mode == 2 ? Math.Min(580, capsuleWidth) : mode == 0 ? 432 : 268);
        width = Math.Max(mode == 2 ? 80 : 240, width);
        var header = mode == 2 ? 0 : 68;
        var y = (double)header; var capsuleX = 8d; var capsuleY = 8d;
        foreach (var model in visible)
        {
            double x, cardY, w, h;
            if (mode == 2)
            {
                // Keep all dynamic windows in capsule; wrap chips without hiding data.
                w = Math.Min(width - 16, ChipWidth(model));
                h = 48;
                if (capsuleX + w > width - 8 && capsuleX > 8) { capsuleX = 8; capsuleY += 56; }
                x = capsuleX; cardY = capsuleY; capsuleX += w + 8;
                y = capsuleY + h + 8;
            }
            else
            {
                x = 16; cardY = y; w = width - 32;
                h = mode == 0 ? 80 + model.Windows.Length * 42 + (ShowQuotaState(model) ? 23 : 0) + (model.Tokens.Length > 0 ? TokenSectionHeight : 0) + 22
                    : Math.Max(64, Math.Max(1, model.Windows.Length) * 32 + 16);
                y += h + (mode == 0 ? 12 : 8);
            }
            if (!cards.TryGetValue(model.Id, out var card))
            {
                var b = new Button { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Opacity = 1,
                    FocusVisualStyle = null, Cursor = Cursors.Hand };
                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Brushes.Transparent); template.VisualTree = border; b.Template = template;
                var id = model.Id;
                b.Click += (_, _) => { if (cards.TryGetValue(id, out var current) && current.Model.Selectable && !current.Model.Active && !busy) ActivateAccount?.Invoke(id); };
                b.GotKeyboardFocus += (_, _) => { EnsureVisible(id); Present(); }; b.LostKeyboardFocus += (_, _) => Present();
                ToolTipService.SetShowOnDisabled(b, true);
                card = new(model, new(x, cardY, w, h), b); cards[id] = card; hits.Children.Add(b);
            }
            card.Model = model; card.Pose.Aim(x, cardY, w, h);
            card.Hit.IsEnabled = model.Selectable && !busy;
            AutomationProperties.SetName(card.Hit, model.Name + (model.Active ? "，当前账户" : "，选择账户"));
            AutomationProperties.SetHelpText(card.Hit, model.Description); card.Hit.ToolTip = model.Description;
        }
        if (visible.Length == 0) y = header + 64;
        contentHeight = y - header;
        var footer = mode == 2 ? 0 : 90;
        var available = ActualHeight > 0 ? ActualHeight - 20 : 700;
        var height = Math.Min(available, y + footer + (mode == 2 ? 0 : 8));
        Shape.Aim(width, Math.Max(64, height), mode == 2 ? 30 : 26, mode == 2 ? 0 : 1, mode == 0 ? 1 : 0);
        viewportHeight = Math.Max(32, height - header - footer);
        scrollMotion.Aim(Math.Clamp(requestedScroll, 0, Math.Max(0, contentHeight - viewportHeight)));
        if (!animate || highContrast || !SystemParameters.ClientAreaAnimation || !IsVisible) { Stop(); SnapMotion(); }
        else Start();
        Present();
    }
    private double ChipWidth(PoolAccountView a)
    {
        var name = new FormattedText(a.Name, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // 12px padding on either side, a 25px bar and an 8px name/bar gap.
        return Math.Clamp(Math.Ceiling(name.WidthIncludingTrailingWhitespace) + 57, 64, 135);
    }
    private void UpdateTip() => ToolTip = notice.Length > 0 ? notice : automatic ? "自动切换已开启 · Ctrl+M 切换布局" : "自动切换已关闭 · Ctrl+M 切换布局";
    private static bool ShowQuotaState(PoolAccountView a) => a.Windows.Length == 0 || a.QuotaState.StartsWith("Credits", StringComparison.Ordinal);
    private void EnsureVisible(string id)
    {
        var c = cards[id]; var y = c.Pose.Value[1] - (mode == 2 ? 0 : 68);
        var next = scroll;
        if (y < next) next = y;
        if (y + c.Pose.Value[3] > next + viewportHeight) next = y + c.Pose.Value[3] - viewportHeight;
        SetScroll(next);
    }
    private void SetScroll(double value)
    { requestedScroll = Math.Clamp(value, 0, Math.Max(0, contentHeight - viewportHeight)); scrollMotion.Aim(requestedScroll); scrollMotion.Snap(); }
    internal void RebaseHost(double x, double y, bool animate)
    {
        // Cancel a one-time native move in scene coordinates, then smoothly reach
        // the safe target. The visible frame keeps its current screen position.
        hostOffset.Value[0] -= x; hostOffset.Value[1] -= y;
        if (animate && !highContrast && SystemParameters.ClientAreaAnimation && IsVisible) Start(); else hostOffset.Snap();
        Present();
    }
    internal void ResetHostOffset() { hostOffset.Snap(); Present(); }
    internal void PrepareEntrance()
    {
        if (IsVisible || highContrast || !SystemParameters.ClientAreaAnimation) return;
        // Hidden state updates already settled the layout. Restore a short starting
        // height so every opening unfolds, including a warmed tray client.
        Shape.Value[1] = Math.Min(140, Shape.Target[1] / 2);
        Shape.Velocity[1] = 0;
        entrancePending = true;
        entrance.Aim(0); entrance.Snap(); entrance.Aim(1); Present();
    }
    internal void BeginEntrance()
    {
        entrancePending = false;
        if (IsVisible && !entrance.Settled) Start();
    }
    private void SnapMotion()
    { entrancePending = false; Shape.Snap(); scrollMotion.Snap(); hostOffset.Snap(); entrance.Snap(); foreach (var c in cards.Values) c.Pose.Snap(); }
    private void Start() { if (Animating || entrancePending) return; Animating = true; watch.Restart(); last = -1; renderingTime = default; CompositionTarget.Rendering += Tick; }
    internal void Stop() { Animating = false; CompositionTarget.Rendering -= Tick; }
    private void Tick(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime; if (time == renderingTime) return; renderingTime = time;
        var now = watch.Elapsed.TotalSeconds;
        if (last < 0) { last = now; return; } // Initial layout/show cost must not consume the visible animation.
        var dt = now - last; last = now; if (dt <= 0) return;
        Shape.Step(dt); scrollMotion.Step(dt); hostOffset.Step(dt); entrance.Step(dt); foreach (var c in cards.Values) c.Pose.Step(dt);
        if (Shape.Settled && scrollMotion.Settled && hostOffset.Settled && entrance.Settled && cards.Values.All(c => c.Pose.Settled)) { SnapMotion(); Stop(); }
        Present();
    }
    private void Present()
    {
        Opacity = Math.Clamp(entrance.Value[0], 0, 1);
        entranceTranslation.Y = (AtTop ? -1 : 1) * 8 * (1 - Opacity);
        var root = SurfaceBounds; var v = Shape.Value;
        viewport = new(root.X, root.Y + v[3] * 68, root.Width, Math.Max(1, root.Height - v[3] * 158));
        hits.Clip = new RectangleGeometry(viewport);
        foreach (var c in cards.Values)
        {
            var p = c.Pose.Value; SetLeft(c.Hit, root.X + p[0]); SetTop(c.Hit, root.Y + p[1] - scroll);
            c.Hit.Width = Math.Max(1, p[2]); c.Hit.Height = Math.Max(1, p[3]);
        }
        var headerIndex = 0; var bottomIndex = 0;
        foreach (var (button, action) in actions)
        {
            button.Opacity = Math.Clamp(v[3], 0, 1); button.IsHitTestVisible = v[3] > .95; button.Focusable = v[3] > .95;
            button.Visibility = v[3] < .005 ? Visibility.Collapsed : Visibility.Visible;
            if (action is "mode" or "theme" or "pin" or "close") { SetLeft(button, root.Right - 128 + headerIndex++ * 29); SetTop(button, root.Y + 13); }
            else
            {
                const int count = 2;
                button.Width = Math.Min(84, (root.Width - 36) / count);
                SetLeft(button, root.Right - 14 - (count - bottomIndex++) * (button.Width + 3));
                SetTop(button, root.Bottom - 49);
                if (action == "refresh") button.IsEnabled = !busy;
            }
        }
        drawing.InvalidateVisual(); BoundsChanged?.Invoke();
    }
    private MediaBrush Foreground => highContrast ? SystemColors.WindowTextBrush : B(light ? "#1D293B" : "#EFF4FD");
    private MediaBrush Muted => highContrast ? SystemColors.WindowTextBrush : B(light ? "#56677D" : "#95A6BE");
    private static SolidColorBrush B(string color) => brushes.GetOrAdd(color, static key =>
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(key)); brush.Freeze(); return brush; });
    private static Pen P(MediaBrush brush, double thickness) => pens.GetOrAdd((brush.ToString(), thickness), static (key, source) =>
    { var pen = new Pen(source, key.Item2); pen.Freeze(); return pen; }, brush);
    private static LinearGradientBrush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
        brush.Freeze(); return brush;
    }
    private void Text(DrawingContext dc, string value, Point point, double size, MediaBrush brush, double opacity = 1, double maxWidth = 1000, bool bold = false, bool trim = false)
    {
        if (opacity <= .005 || maxWidth < 1 || string.IsNullOrEmpty(value)) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = value + "|" + size + "|" + brush + "|" + bold + "|" + dpi + (trim ? "|" + Math.Round(maxWidth) : "");
        if (!text.TryGetValue(key, out var ft))
        {
            if (text.Count > 1024) text.Clear();
            ft = new(value, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, dpi)
            { MaxTextWidth = trim ? Math.Max(1, Math.Round(maxWidth)) : 2400, MaxLineCount = 1, Trimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None };
            text[key] = ft;
        }
        dc.PushOpacity(Math.Clamp(opacity, 0, 1));
        dc.PushClip(new RectangleGeometry(new(point.X, point.Y, maxWidth, ft.Height + 2)));
        dc.DrawText(ft, point); dc.Pop(); dc.Pop();
    }
    private void Draw(DrawingContext dc)
    {
        var root = SurfaceBounds; var s = Shape.Value; var fg = Foreground; var muted = Muted;
        if (!highContrast) for (var i = 8; i > 0; i--) dc.DrawRoundedRectangle(B("#05000000"), null, new(root.X - i * .5, root.Y - i * .3, root.Width + i, root.Height + i), s[2] + i * .5, s[2] + i * .5);
        dc.DrawRoundedRectangle(highContrast ? SystemColors.WindowBrush : light ? lightBackground : darkBackground,
            P(highContrast ? SystemColors.WindowTextBrush : B(light ? "#BEB3C8E3" : "#5A8BAAD6"), 1), root, s[2], s[2]);
        dc.PushClip(new RectangleGeometry(root, s[2], s[2]));
        Text(dc, "Codex Pool", new(root.X + 20, root.Y + 15), 18, fg, s[3], root.Width - 158, true);
        Text(dc, status, new(root.X + 20, root.Y + 42), 10, muted, s[3], root.Width - 40);
        dc.PushClip(new RectangleGeometry(viewport));
        foreach (var c in cards.Values)
        {
            var a = c.Model; var p = c.Pose.Value; var r = new Rect(root.X + p[0], root.Y + p[1] - scroll, p[2], p[3]);
            if (!r.IntersectsWith(viewport)) continue;
            dc.PushOpacity(a.Selectable ? 1 : .55);
            var active = a.Active && a.Selectable;
            var accent = highContrast ? SystemColors.HighlightBrush : B(light ? "#679DD8" : "#82B9FF");
            dc.DrawRoundedRectangle(highContrast ? SystemColors.WindowBrush : B(active ? light ? "#EBD0E4FF" : "#BE345687" : light ? "#87FFFFFF" : "#19748FB8"),
                P(c.Hit.IsKeyboardFocused ? fg : active ? accent : B("#338297B8"), c.Hit.IsKeyboardFocused ? 2 : 1), r, 14, 14);
            dc.PushClip(new RectangleGeometry(r, 14, 14));
            var compact = Math.Clamp(s[3] - s[4], 0, 1); var capsule = 1 - s[3];
            var leftWidth = (r.Width - 26) * (1 - compact * .64) - capsule * 31;
            var credits = a.QuotaState.StartsWith("Credits", StringComparison.Ordinal);
            var identityHeight = credits ? 50 : 36;
            var nameY = r.Y + 8 + capsule * 8 + compact * (Math.Max(8, (r.Height - identityHeight) / 2) - 8);
            Text(dc, a.Name, new(r.X + 12, nameY), 13, fg, 1, leftWidth, true, !Animating);
            var shortWindow = a.Windows.Where(w => w.Family == "codex" && w.Seconds != 604800)
                .OrderByDescending(w => w.Kind is "5h" or "primary_window").ThenBy(w => w.Seconds ?? int.MaxValue).FirstOrDefault();
            var weeklyWindow = a.Windows.FirstOrDefault(w => w.Family == "codex" && w.Seconds == 604800);
            MiniBar(dc, shortWindow?.Remaining, new(r.Right - 37, r.Y + 16, 25, 4), capsule, accent);
            MiniBar(dc, weeklyWindow?.Remaining, new(r.Right - 37, r.Y + 29, 25, 4), capsule, B("#7FD4B4"));
            Text(dc, a.Status, new(r.X + 12, nameY + (credits ? 36 : 22)), 10, muted, compact, leftWidth, trim: !Animating);
            Text(dc, a.Plan + " · " + a.Status, new(r.X + 12, r.Y + 29), 10, muted, s[4], r.Width - 24);
            Text(dc, a.SubscriptionDeadline, new(r.X + 12, r.Y + 46), 10, muted, s[4], r.Width - 24, trim: !Animating);
            var rowY = 8 + s[4] * 64;
            var quotaX = r.X + 12 + compact * (r.Width * .38 + 22);
            foreach (var w in a.Windows)
            {
                if (compact > .005)
                {
                    var center = new Point(r.X + r.Width * .38 + 14, r.Y + rowY + 12);
                    dc.PushOpacity(compact);
                    dc.DrawEllipse(null, P(B("#337C9ABB"), 3), center, 12, 12);
                    if (w.Remaining is { } remaining && remaining > 0)
                    {
                        dc.PushTransform(new TranslateTransform(center.X - 14, center.Y - 14));
                        var key = Math.Round(remaining, 1);
                        if (!rings.TryGetValue(key, out var geometry)) { geometry = QuotaIndicator.Ring(key, 28, 3); geometry.Freeze(); rings[key] = geometry; }
                        dc.DrawGeometry(null, P(highContrast ? SystemColors.HighlightBrush : B(remaining < 15 ? "#FF7069" : remaining < 30 ? "#FFC65F" : "#7DB8FF"), 3), geometry); dc.Pop();
                    }
                    dc.Pop();
                }
                var label = compact > .95 && w.Family != "codex" ? w.Label.Replace("附加额度", "附加", StringComparison.Ordinal) : w.Label;
                Text(dc, label + "  " + QuotaDisplay.Percent(w) + (compact > .95 && w.Family == "codex" ? " 剩余" : ""), new(quotaX, r.Y + rowY - compact * 3), 11, fg, s[3], r.Right - 12 - quotaX, trim: !Animating);
                Text(dc, PoolPresentation.Reset(w), new(r.Right - 136, r.Y + rowY), 10, muted, s[4], 124);
                Text(dc, PoolPresentation.Reset(w), new(quotaX, r.Y + rowY + 14), 9, muted, compact, r.Right - 12 - quotaX);
                dc.PushOpacity(Math.Clamp(s[4], 0, 1));
                dc.DrawRoundedRectangle(B("#337C9ABB"), null, new(r.X + 12, r.Y + rowY + 21, Math.Max(1, r.Width - 24), 4), 2, 2);
                if (w.Remaining is { } amount) dc.DrawRoundedRectangle(highContrast ? SystemColors.HighlightBrush : B(amount < 15 ? "#FFFF7069" : amount < 30 ? "#FFFFC65F" : "#FF7DB8FF"), null,
                    new(r.X + 12, r.Y + rowY + 21, Math.Max(0, r.Width - 24) * amount / 100, 4), 2, 2);
                dc.Pop(); rowY += 36 + s[4] * 6 - compact * 4;
            }
            if (ShowQuotaState(a))
            {
                var move = credits ? compact : 0;
                var stateX = quotaX + (r.X + 12 - quotaX) * move;
                var stateY = r.Y + rowY + (nameY + 20 - r.Y - rowY) * move;
                var stateWidth = (r.Right - 12 - quotaX) * (1 - move) + leftWidth * move;
                Text(dc, a.QuotaState, new(stateX, stateY), 10, muted, s[3], stateWidth, trim: !Animating);
                rowY += 23;
            }
            if (a.Tokens.Length > 0)
            {
                Text(dc, a.Tokens, new(r.X + 12, r.Y + rowY), 10, muted, s[4], r.Width - 24);
                if (a.TokenCache is { } cache) TokenBar(dc, cache, new(r.X + 12, r.Y + rowY + 20, r.Width - 24, 6), s[4]);
                rowY += TokenSectionHeight;
            }
            Text(dc, a.Updated, new(r.X + 12, r.Y + rowY), 10, muted, s[4], r.Width - 24);
            dc.Pop(); dc.Pop();
        }
        if (cards.Count == 0) Text(dc, "没有可用账户，请打开 Web 面板添加。", new(root.X + 18, viewport.Y + 12), 12, muted, 1, root.Width - 36);
        dc.Pop();
        if (contentHeight > viewportHeight)
        {
            var length = Math.Max(16, viewport.Height * viewportHeight / contentHeight);
            var top = viewport.Y + (viewport.Height - length) * Math.Clamp(scroll / Math.Max(1, contentHeight - viewportHeight), 0, 1);
            dc.DrawRoundedRectangle(muted, null, new(viewport.Right - 7, top, 2, length), 1, 1);
        }
        Text(dc, "已启用 " + accounts.Count(a => a.Enabled) + " 个账户 · " + runningRequests + " 个请求处理中",
            new(root.X + 16, root.Bottom - 86), 10, muted, s[3], root.Width - 32);
        Text(dc, automatic ? "自动切换 · 开启" : "自动切换 · 关闭", new(root.X + 16, root.Bottom - 65), 10, muted, s[3], root.Width - 32);
        if (notice.Length > 0) Text(dc, notice, new(root.X + 16, root.Bottom - 21), 9, muted, s[3], root.Width - 32);
        dc.Pop();
    }
    private void TokenBar(DrawingContext dc, TokenCacheSplit cache, Rect rect, double opacity)
    {
        if (opacity < .005 || rect.Width <= 0) return;
        var cached = highContrast ? SystemColors.HighlightBrush : B(light ? "#238B71" : "#7FD4B4");
        var uncached = highContrast ? SystemColors.WindowTextBrush : B(light ? "#477DC0" : "#82B9FF");
        dc.PushOpacity(Math.Clamp(opacity, 0, 1));
        dc.DrawRoundedRectangle(B("#337C9ABB"), null, rect, 3, 3);
        if (cache.CachedPercent is { } percent)
        {
            var left = rect.Width * percent / 100;
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            if (left > 0) dc.DrawRectangle(cached, null, new(rect.X, rect.Y, left, rect.Height));
            if (left < rect.Width) dc.DrawRectangle(uncached, null, new(rect.X + left, rect.Y, rect.Width - left, rect.Height));
            dc.Pop();
        }
        var half = rect.Width / 2;
        Text(dc, cache.CachedLabel, new(rect.X, rect.Bottom + 7), 9, cache.Cached is null ? Muted : cached, maxWidth: half - 4);
        Text(dc, cache.UncachedLabel, new(rect.X + half, rect.Bottom + 7), 9, cache.Uncached is null ? Muted : uncached, maxWidth: half);
        dc.Pop();
    }
    private static void MiniBar(DrawingContext dc, double? value, Rect rect, double opacity, MediaBrush color)
    {
        if (opacity < .005) return;
        dc.PushOpacity(Math.Clamp(opacity, 0, 1));
        dc.DrawRoundedRectangle(B("#557C9ABB"), null, rect, 2, 2);
        if (value is { } amount) dc.DrawRoundedRectangle(color, null, new(rect.X, rect.Y, rect.Width * amount / 100, rect.Height), 2, 2);
        dc.Pop();
    }
    private sealed class SceneDrawing(PoolScene owner) : FrameworkElement
    { protected override void OnRender(DrawingContext context) => owner.Draw(context); }
}
