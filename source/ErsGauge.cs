using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class ErsGauge : Control
{
    float? percentage;
    internal float? Percentage
    {
        get { return percentage; }
        set { percentage = value; Invalidate(); }
    }

    internal ErsGauge()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        MinimumSize = new Size(180, 24);
        AccessibleName = "ERS level";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle body = new Rectangle(1, 2, Width - 9, Height - 5);
        using (var background = new SolidBrush(Color.FromArgb(43, 50, 62))) e.Graphics.FillRectangle(background, body);
        if (percentage.HasValue)
        {
            float clamped = Math.Max(0F, Math.Min(100F, percentage.Value));
            int fill = (int)Math.Round((body.Width - 4) * clamped / 100F);
            Color color = clamped <= 15F ? Color.FromArgb(236, 77, 88) :
                clamped <= 40F ? Color.FromArgb(245, 176, 65) : Color.FromArgb(39, 210, 151);
            using (var brush = new LinearGradientBrush(new Rectangle(body.X + 2, body.Y + 2, Math.Max(1, fill), body.Height - 4),
                color, Color.FromArgb(Math.Max(0, color.R - 32), Math.Max(0, color.G - 32), Math.Max(0, color.B - 32)), LinearGradientMode.Horizontal))
                if (fill > 0) e.Graphics.FillRectangle(brush, body.X + 2, body.Y + 2, fill, body.Height - 4);
        }
        using (var pen = new Pen(Color.FromArgb(105, 116, 133), 1F)) e.Graphics.DrawRectangle(pen, body);
        using (var terminal = new SolidBrush(Color.FromArgb(105, 116, 133))) e.Graphics.FillRectangle(terminal, Width - 7, Height / 3, 6, Height / 3);
    }
}
