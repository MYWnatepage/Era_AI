using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MinorShift.Emuera.NativeDisplay;

internal static class MusicSpeakerButton
{
    public static Rectangle Bounds(Rectangle area, int lineHeight)
    {
        int size = Math.Clamp(lineHeight + 12, 32, 64);
        return new Rectangle(Math.Max(0, area.Right - size - 12), area.Top + 12, size, size);
    }
    public static void Draw(Graphics graphics, Rectangle bounds, DisplayTheme theme, bool muted, bool hovered)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds);
            using (var background = new SolidBrush(theme.BackgroundColor)) graphics.FillRectangle(background, bounds);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 44f, bounds.Height / 44f);
            Color color = hovered ? ColorSafety.Selection(theme) : theme.TextColor;
            using var pen = new Pen(color, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            graphics.DrawPolygon(pen, [new(9, 17), new(15, 17), new(23, 10), new(23, 34), new(15, 27), new(9, 27)]);
            if (muted)
            {
                graphics.DrawLine(pen, 29, 17, 37, 27);
                graphics.DrawLine(pen, 37, 17, 29, 27);
            }
            else
            {
                graphics.DrawArc(pen, 19, 14, 14, 16, -60, 120);
                graphics.DrawArc(pen, 18, 8, 24, 28, -60, 120);
            }
        }
        finally { graphics.Restore(state); }
    }
}
