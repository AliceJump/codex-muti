using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CodexPool.Floating;

// Windows associates visibility preferences with the executable path and icon GUID.
// The launch scripts supply a stable path; this GUID must remain unchanged on upgrades.
internal sealed class StableTrayIcon : Forms.NativeWindow, IDisposable
{
    internal static readonly Guid Identity = new("552bb14d-21b7-4a30-aed2-641f1fd39422");
    private const uint Callback = 0x8001;
    private const uint GuidFlag = 0x20;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly NotifyCall notify;
    private System.Drawing.Icon icon;
    private string text;
    private bool registered, disposed;
    public Forms.ContextMenuStrip? ContextMenuStrip { get; set; }
    public event EventHandler<Forms.MouseEventArgs>? MouseClick;
    public event EventHandler<Forms.MouseEventArgs>? ContextMenuRequested;

    internal delegate bool NotifyCall(uint operation, ref NotifyData data);
    public StableTrayIcon(System.Drawing.Icon icon, string text, NotifyCall? notify = null)
    {
        this.notify = notify ?? Shell_NotifyIcon;
        this.icon = icon; this.text = text;
        // A hidden top-level window also receives Explorer's restart broadcast.
        CreateHandle(new Forms.CreateParams { Caption = "Codex Pool · Tray", ExStyle = 0x80 });
        Publish();
    }
    public System.Drawing.Icon Icon
    {
        get => icon;
        set { icon = value; Publish(); }
    }
    public string Text
    {
        get => text;
        set { text = value; Publish(); }
    }
    private NotifyData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyData>(), Window = Handle, Id = 1,
        Flags = flags | GuidFlag, CallbackMessage = Callback, Icon = icon.Handle,
        Tip = text.Length > 127 ? text[..127] : text, Info = "", InfoTitle = "", Guid = Identity
    };
    private void Publish()
    {
        if (disposed) return;
        var data = Data(0x01 | 0x02 | 0x04 | 0x80); // message, icon, tooltip, show tooltip in v4
        if (registered && notify(1, ref data)) return;
        registered = false;
        registered = notify(0, ref data);
        if (!registered) return;
        data.Version = 4;
        _ = notify(4, ref data);
    }
    public void ShowBalloon(string title, string message)
    {
        if (disposed || !registered) return;
        var data = Data(0x10);
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = 1;
        _ = notify(1, ref data);
    }
    internal bool TryGetBounds(out System.Drawing.Rectangle bounds)
    {
        var identity = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = Handle, Id = 1, Guid = Identity };
        if (registered && Shell_NotifyIconGetRect(ref identity, out var rect) == 0)
        { bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom); return true; }
        bounds = default; return false;
    }
    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == taskbarCreated) { registered = false; Publish(); }
        if (message.Msg == Callback)
        {
            var notification = (uint)((long)message.LParam & 0xffff);
            if (notification is 0x400 or 0x401) // NIN_SELECT / NIN_KEYSELECT (version 4)
                MouseClick?.Invoke(this, new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1,
                    (short)((long)message.WParam & 0xffff), (short)(((long)message.WParam >> 16) & 0xffff), 0));
            if (notification == 0x7b)
            {
                // Foreground ownership lets an outside click dismiss the menu normally.
                _ = SetForegroundWindow(Handle);
                var args = new Forms.MouseEventArgs(Forms.MouseButtons.Right, 1,
                    (short)((long)message.WParam & 0xffff), (short)(((long)message.WParam >> 16) & 0xffff), 0);
                if (ContextMenuRequested is { } requested) requested.Invoke(this, args);
                else if (ContextMenuStrip is { } menu) menu.Show(Forms.Cursor.Position);
            }
            return;
        }
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (disposed) return;
        if (registered) { var data = Data(0); _ = notify(2, ref data); }
        disposed = true;
        ContextMenuStrip?.Dispose(); DestroyHandle(); icon.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct IconRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identity, out IconRect rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
