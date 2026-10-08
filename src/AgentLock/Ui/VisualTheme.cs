using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace AgentLock.Ui;

/// <summary>Appearance only; does not change window activation, scaling, input, or authentication.</summary>
public static class VisualTheme
{
    public static readonly Color Canvas = Color.FromArgb(245, 248, 247);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(23, 60, 54);
    public static readonly Color Muted = Color.FromArgb(97, 115, 109);
    public static readonly Color Accent = Color.FromArgb(8, 127, 117);
    public static readonly Color AccentSoft = Color.FromArgb(226, 244, 238);
    public static readonly Color Border = Color.FromArgb(222, 232, 226);

    /// <summary>The caller owns each returned font.</summary>
    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style, GraphicsUnit.Point);

    public static void ApplyForm(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.BackColor = Canvas;
        form.ForeColor = Ink;
        form.Font = UiFont(10F);
    }

    public static void StyleButton(Button button, bool primary = false)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Color.White : Ink;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(7, 115, 106) : Canvas;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(6, 101, 93) : AccentSoft;
        button.Font = UiFont(10F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    internal static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (radius <= 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }
        float diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Color ParentSurface(Control control)
    {
        for (Control? ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is RoundedCard card) return card.SurfaceColor;
            if (ancestor.BackColor.A == 255) return ancestor.BackColor;
        }
        return Canvas;
    }
}

/// <summary>A normal Panel with a rounded surface; children retain their standard input behavior.</summary>
public sealed class RoundedCard : Panel
{
    private float cornerRadius = 16;
    private Color surfaceColor = VisualTheme.Surface, borderColor = VisualTheme.Border;

    [DefaultValue(16F)]
    public float CornerRadius { get => cornerRadius; set { cornerRadius = Math.Max(0, value); Invalidate(); } }
    [DefaultValue(typeof(Color), "White")]
    public Color SurfaceColor { get => surfaceColor; set { surfaceColor = value; Invalidate(); } }
    [DefaultValue(typeof(Color), "222, 232, 226")]
    public Color BorderColor { get => borderColor; set { borderColor = value; Invalidate(); } }

    public RoundedCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width > 1 && ClientSize.Height > 1)
        {
            var state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = VisualTheme.RoundedRectangle(new RectangleF(.5F, .5F,
                    ClientSize.Width - 1F, ClientSize.Height - 1F), cornerRadius * DeviceDpi / 96F);
                using var fill = new SolidBrush(surfaceColor);
                using var edge = new Pen(borderColor);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
            }
            finally { e.Graphics.Restore(state); }
        }
        base.OnPaint(e);
    }
}

/// <summary>Button appearance only. Standard Click, keyboard, focus, and accessibility remain inherited.</summary>
public sealed class RoundedButton : Button
{
    private bool hovered;
    private float cornerRadius = 12;

    [DefaultValue(12F)]
    public float CornerRadius { get => cornerRadius; set { cornerRadius = Math.Max(0, value); Invalidate(); } }

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        VisualTheme.StyleButton(this);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;
        var state = e.Graphics.Save();
        try
        {
            e.Graphics.Clear(VisualTheme.ParentSurface(this));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool pressed = hovered && (Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) != 0;
            Color fillColor = !Enabled ? VisualTheme.AccentSoft
                : pressed ? FlatAppearance.MouseDownBackColor
                : hovered ? FlatAppearance.MouseOverBackColor : BackColor;
            using var shape = VisualTheme.RoundedRectangle(new RectangleF(.5F, .5F,
                ClientSize.Width - 1F, ClientSize.Height - 1F), cornerRadius * DeviceDpi / 96F);
            using var fill = new SolidBrush(fillColor);
            e.Graphics.FillPath(fill, shape);
            if (FlatAppearance.BorderSize > 0)
            {
                using var edge = new Pen(Enabled ? FlatAppearance.BorderColor : VisualTheme.Border,
                    FlatAppearance.BorderSize);
                e.Graphics.DrawPath(edge, shape);
            }
            var textBounds = Rectangle.FromLTRB(Padding.Left + 8, Padding.Top + 2,
                ClientSize.Width - Padding.Right - 8, ClientSize.Height - Padding.Bottom - 2);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                Enabled ? ForeColor : VisualTheme.Muted, flags);
            if (Focused && ShowFocusCues)
            {
                var focus = Rectangle.Inflate(ClientRectangle, -6, -6);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, Enabled ? ForeColor : VisualTheme.Muted, fillColor);
            }
        }
        finally { e.Graphics.Restore(state); }
    }
}
