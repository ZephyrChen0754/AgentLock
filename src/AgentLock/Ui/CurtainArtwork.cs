namespace AgentLock.Ui;

/// <summary>Only draws the curtain artwork; does not create windows or change protection behavior.</summary>
public static class CurtainArtwork
{
    public static void Draw(Graphics graphics, Size size)
    {
        int width = Math.Max(1, size.Width - 64);
        int centerX = size.Width / 2;
        int centerY = size.Height / 2;
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        graphics.Clear(Color.Black);
        BrandMark.Draw(graphics, new RectangleF(centerX - 36, centerY - 166, 72, 72));
        using var brandFont = VisualTheme.UiFont(11, FontStyle.Bold);
        using var titleFont = VisualTheme.UiFont(22, FontStyle.Bold);
        using var hintFont = VisualTheme.UiFont(11);
        using var keyFont = new Font("Consolas", 11, FontStyle.Bold);
        TextRenderer.DrawText(graphics, "AgentLock", brandFont, new Rectangle(32, centerY - 83, width, 28),
            Color.FromArgb(159, 186, 174), flags);
        TextRenderer.DrawText(graphics, "本地操作已保护", titleFont, new Rectangle(32, centerY - 42, width, 52),
            Color.FromArgb(239, 246, 241), flags);
        TextRenderer.DrawText(graphics, "你的应用和 Agent 任务留在当前桌面", hintFont,
            new Rectangle(32, centerY + 17, width, 34), Color.FromArgb(143, 165, 154), flags);
        var keyBounds = new RectangleF(centerX - 188, centerY + 75, 52, 34);
        using var keyPath = VisualTheme.RoundedRectangle(keyBounds, 8);
        using var keyFill = new SolidBrush(Color.FromArgb(23, 41, 34));
        using var keyEdge = new Pen(Color.FromArgb(46, 72, 60));
        graphics.FillPath(keyFill, keyPath);
        graphics.DrawPath(keyEdge, keyPath);
        TextRenderer.DrawText(graphics, "F12", keyFont, Rectangle.Round(keyBounds),
            Color.FromArgb(214, 237, 225), flags);
        TextRenderer.DrawText(graphics, "输入保护密码，恢复键鼠操作", hintFont,
            new Rectangle(centerX - 124, centerY + 73, 320, 38), Color.FromArgb(191, 212, 200),
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
