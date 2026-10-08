using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AgentLock.Ui;

/// <summary>Vector artwork shared by the app and the reproducible brand renderer.</summary>
public static class BrandMark
{
    public static readonly Color Mint = Color.FromArgb(103, 216, 180);
    private static readonly Lazy<Icon> CachedIcon = new(ReadEmbeddedIcon);

    public static void Draw(Graphics graphics, RectangleF bounds, bool tile = true)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        float size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0) return;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.TranslateTransform(bounds.X + (bounds.Width - size) / 2,
                bounds.Y + (bounds.Height - size) / 2);
            graphics.ScaleTransform(size / 512F, size / 512F);
            if (tile)
            {
                using var background = VisualTheme.RoundedRectangle(new RectangleF(0, 0, 512, 512), 112);
                using var fill = new SolidBrush(VisualTheme.Ink);
                graphics.FillPath(fill, background);
            }
            // A sheltering arch and a small forward cue: no system shield or padlock.
            using var arch = new GraphicsPath();
            arch.AddLine(128, 376, 198, 190);
            arch.AddBezier(198, 190, 210, 152, 232, 130, 256, 130);
            arch.AddBezier(256, 130, 280, 130, 302, 152, 314, 190);
            arch.AddLine(314, 190, 384, 376);
            using var stroke = new Pen(tile ? Color.White : VisualTheme.Ink, 44)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawPath(stroke, arch);
            using var cue = new GraphicsPath();
            cue.AddPolygon([new PointF(236, 238), new PointF(320, 288), new PointF(236, 338)]);
            using var mint = new SolidBrush(Mint);
            graphics.FillPath(mint, cue);
        }
        finally { graphics.Restore(state); }
    }

    /// <summary>The caller owns the returned transparent bitmap; tile keeps transparent rounded corners.</summary>
    public static Bitmap CreateBitmap(int size, bool tile = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            Draw(graphics, new RectangleF(0, 0, size, size), tile);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    /// <summary>Each caller owns a clone; the embedded icon stream is not kept open.</summary>
    public static Icon LoadIcon() => (Icon)CachedIcon.Value.Clone();

    private static Icon ReadEmbeddedIcon()
    {
        using var stream = typeof(BrandMark).Assembly.GetManifestResourceStream("AgentLock.Brand.ico")
            ?? throw new InvalidOperationException("缺少 AgentLock.Brand.ico 品牌资源。");
        using var loaded = new Icon(stream);
        return (Icon)loaded.Clone();
    }
}
