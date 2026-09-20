using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal enum OverlayCorner
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
    TopCenter,
    LeftHudGap
}

// Display-only window. It consumes immutable controller snapshots and never
// reads or writes game memory itself.
internal sealed class ErsOverlay : Form
{
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct PointNative { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr window, ref PointNative point);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
        int width, int height, uint flags);

    const int Transparent = 0x20;
    const int ToolWindow = 0x80;
    const int NoActivate = 0x08000000;
    const int LogicalWidth = 300;
    const int LogicalHeight = 48;

    ErsSnapshot displayed;
    ErsTarget displayedTarget = ErsTarget.Automatic;
    float scale = 1F;
    internal string TargetHint = "Ctrl+F12";

    readonly Font titleFont =
        new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font valueFont =
        new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font smallFont =
        new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font hintFont =
        new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Pixel);

    internal ErsOverlay()
    {
        Text = "F1 ERS Manager overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(LogicalWidth, LogicalHeight);
        BackColor = Color.FromArgb(20, 23, 29);
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    sealed class DpiScope : IDisposable
    {
        readonly IntPtr previous;

        internal DpiScope()
        {
            try
            {
                previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                if (previous == IntPtr.Zero)
                    previous = SetThreadDpiAwarenessContext(new IntPtr(-3));
                if (previous == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Overlay DPI context is unavailable.");
            }
            catch (EntryPointNotFoundException)
            {
                throw new NotSupportedException(
                    "The overlay requires Windows 10 version 1607 or later.");
            }
        }

        public void Dispose()
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    protected override void CreateHandle()
    {
        using (var dpi = new DpiScope()) base.CreateHandle();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams value = base.CreateParams;
            value.ExStyle |= Transparent | ToolWindow | NoActivate;
            return value;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084)
        {
            message.Result = new IntPtr(-1);
            return;
        }
        if (message.Msg == 0x0021)
        {
            message.Result = new IntPtr(3);
            return;
        }
        if (message.Msg == 0x02E0)
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    internal static bool CanDisplay(ErsSnapshot snapshot, bool enabled,
        int foregroundProcessId, long now, long frequency)
    {
        return enabled && snapshot != null && snapshot.Supported &&
            snapshot.VerifiedProcessId > 0 &&
            foregroundProcessId == snapshot.VerifiedProcessId &&
            snapshot.Car1.Available && snapshot.Car2.Available &&
            ValidPercentage(snapshot.Car1.Percentage) &&
            ValidPercentage(snapshot.Car2.Percentage) &&
            frequency > 0 && snapshot.VerifiedStamp > 0 &&
            now >= snapshot.VerifiedStamp &&
            now - snapshot.VerifiedStamp <= frequency * 4 / 5;
    }

    static bool ValidPercentage(float? value)
    {
        return value.HasValue && !Single.IsNaN(value.Value) &&
            !Single.IsInfinity(value.Value) &&
            value.Value >= 0F && value.Value <= 100.1F;
    }

    internal static Rectangle CalculateBounds(Rectangle area,
        OverlayCorner corner, int dpi)
    {
        if (dpi < 96 || dpi > 768) dpi = 96;
        double factor = dpi / 96.0;
        int width = (int)Math.Round(LogicalWidth * factor);
        int height = (int)Math.Round(LogicalHeight * factor);
        if (area.Width < width || area.Height < height)
            return Rectangle.Empty;
        if (corner < OverlayCorner.TopRight ||
            corner > OverlayCorner.LeftHudGap)
            throw new ArgumentOutOfRangeException("corner");

        int horizontal = Math.Min((int)Math.Round(12 * factor),
            (area.Width - width) / 2);
        int vertical = Math.Min((int)Math.Round(84 * factor),
            (area.Height - height) / 4);
        bool left = corner == OverlayCorner.TopLeft ||
            corner == OverlayCorner.BottomLeft ||
            corner == OverlayCorner.LeftHudGap;
        bool top = corner == OverlayCorner.TopLeft ||
            corner == OverlayCorner.TopRight ||
            corner == OverlayCorner.TopCenter;
        int x = corner == OverlayCorner.TopCenter
            ? area.Left + (area.Width - width) / 2
            : left ? area.Left + horizontal : area.Right - horizontal - width;
        int y = corner == OverlayCorner.LeftHudGap
            ? area.Top + (area.Height - height) * 3 / 4
            : top ? area.Top + vertical : area.Bottom - vertical - height;
        return new Rectangle(x, y, width, height);
    }

    internal void RefreshDisplay(ErsSnapshot snapshot, bool enabled,
        OverlayCorner corner, ErsTarget selectedTarget)
    {
        if (!enabled || snapshot == null)
        {
            Hide();
            return;
        }
        using (var dpi = new DpiScope())
            RefreshCore(snapshot, corner, selectedTarget);
    }

    void RefreshCore(ErsSnapshot snapshot, OverlayCorner corner,
        ErsTarget selectedTarget)
    {
        IntPtr target = GetForegroundWindow();
        uint processId;
        GetWindowThreadProcessId(target, out processId);
        if (!CanDisplay(snapshot, true, (int)processId,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency) ||
            target == IntPtr.Zero || IsIconic(target) ||
            !IsWindowVisible(target))
        {
            Hide();
            return;
        }

        Rect client;
        var origin = new PointNative();
        if (!GetClientRect(target, out client) ||
            !ClientToScreen(target, ref origin))
        {
            Hide();
            return;
        }
        Rectangle area = new Rectangle(origin.X, origin.Y,
            client.Right - client.Left, client.Bottom - client.Top);
        IntPtr monitor = MonitorFromWindow(target, 2);
        var info = new MonitorInfo {
            Size = (uint)Marshal.SizeOf(typeof(MonitorInfo))
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            Hide();
            return;
        }
        area = Rectangle.Intersect(area, new Rectangle(info.Monitor.Left,
            info.Monitor.Top, info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top));

        IntPtr window = Handle;
        if (MonitorFromWindow(window, 2) != monitor &&
            !SetWindowPos(window, IntPtr.Zero, area.Left, area.Top, 0, 0,
                0x0001 | 0x0004 | 0x0010))
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Overlay monitor positioning failed.");

        int dpi = (int)GetDpiForWindow(window);
        Rectangle bounds = CalculateBounds(area, corner, dpi);
        if (bounds.IsEmpty)
        {
            Hide();
            return;
        }
        float newScale = bounds.Width / (float)LogicalWidth;
        if (!Object.ReferenceEquals(displayed, snapshot) ||
            displayedTarget != selectedTarget ||
            Math.Abs(scale - newScale) > 0.001F)
        {
            displayed = snapshot;
            displayedTarget = selectedTarget;
            scale = newScale;
            Invalidate();
        }
        if (Bounds != bounds) Bounds = bounds;
        if (!Visible) Show();
        if (!SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010))
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Overlay positioning failed.");
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int width = ClientSize.Width;
        int height = ClientSize.Height;
        if (width < 12 || height < 12) return;
        float radius = Math.Min(height, 12 * width / (float)LogicalWidth);
        using (var path = new GraphicsPath())
        {
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(width - radius, 0, radius, radius, 270, 90);
            path.AddArc(width - radius, height - radius, radius, radius, 0, 90);
            path.AddArc(0, height - radius, radius, radius, 90, 90);
            path.CloseFigure();
            Region previous = Region;
            Region = new Region(path);
            if (previous != null) previous.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (displayed == null) return;
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(scale, scale);
        using (var path = new GraphicsPath())
        using (var background = new SolidBrush(Color.FromArgb(20, 23, 29)))
        using (var border = new Pen(Color.FromArgb(61, 66, 76)))
        using (var divider = new Pen(Color.FromArgb(54, 61, 72)))
        using (var cyan = new SolidBrush(Color.FromArgb(45, 205, 242)))
        using (var green = new SolidBrush(Color.FromArgb(52, 219, 163)))
        using (var amber = new SolidBrush(Color.FromArgb(244, 184, 77)))
        using (var white = new SolidBrush(Color.White))
        using (var muted = new SolidBrush(Color.FromArgb(153, 160, 174)))
        {
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(287, 0, 12, 12, 270, 90);
            path.AddArc(287, 35, 12, 12, 0, 90);
            path.AddArc(0, 35, 12, 12, 90, 90);
            path.CloseFigure();
            graphics.FillPath(background, path);
            graphics.DrawPath(border, path);
            graphics.DrawString("ERS", titleFont, cyan, 8, 15);
            graphics.DrawLine(divider, 44, 8, 44, 40);
            graphics.DrawLine(divider, 124, 8, 124, 40);
            graphics.DrawLine(divider, 204, 8, 204, 40);

            DrawCar(graphics, displayed.Car1, 1,
                CarLocked(displayed, 1), 51, green, amber, white, muted);
            DrawCar(graphics, displayed.Car2, 2,
                CarLocked(displayed, 2), 131, green, amber, white, muted);
            DrawTarget(graphics, cyan, muted);
        }
    }

    void DrawCar(Graphics graphics, ErsCarState car, int number, bool locked,
        int x, Brush green, Brush amber, Brush white, Brush muted)
    {
        Brush accent = locked ? amber : green;
        graphics.FillEllipse(accent, x, 20, 7, 7);
        graphics.DrawString("CAR " + number, smallFont, muted, x + 11, 5);
        if (locked)
            graphics.DrawString("HOLD", smallFont, amber, x + 43, 5);
        string value = car.Percentage.HasValue
            ? car.Percentage.Value.ToString("0.0") + "%"
            : "—";
        graphics.DrawString(value, valueFont, white, x + 11, 21);
    }

    void DrawTarget(Graphics graphics, Brush cyan, Brush muted)
    {
        string hint = String.IsNullOrWhiteSpace(TargetHint)
            ? String.Empty
            : TargetHint.Trim();
        string caption = hint.Length == 0 ? "TARGET" : hint.ToUpperInvariant();
        using (var centered = new StringFormat())
        {
            centered.Alignment = StringAlignment.Center;
            centered.LineAlignment = StringAlignment.Near;
            centered.Trimming = StringTrimming.EllipsisCharacter;
            centered.FormatFlags = StringFormatFlags.NoWrap;
            graphics.DrawString(caption, smallFont, muted,
                new RectangleF(205, 5, 94, 13), centered);
            graphics.DrawString(TargetLabel(displayedTarget), valueFont, cyan,
                new RectangleF(205, 21, 94, 18), centered);
        }
    }

    internal static string TargetLabel(ErsTarget target)
    {
        switch (target)
        {
            case ErsTarget.Automatic: return "AUTO";
            case ErsTarget.Car1: return "CAR 1";
            case ErsTarget.Car2: return "CAR 2";
            case ErsTarget.Both: return "BOTH";
            default: throw new ArgumentOutOfRangeException("target");
        }
    }

    static bool CarLocked(ErsSnapshot snapshot, int car)
    {
        if (!snapshot.Locked) return false;
        return snapshot.LockedTarget == ErsTarget.Both ||
            (car == 1 && snapshot.LockedTarget == ErsTarget.Car1) ||
            (car == 2 && snapshot.LockedTarget == ErsTarget.Car2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            valueFont.Dispose();
            smallFont.Dispose();
            hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
