using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MinorShift.Emuera.NativeDisplay;

internal static class UsageChartButton
{
    public static Rectangle Bounds(Rectangle area, int lineHeight)
    {
        var speaker = MusicSpeakerButton.Bounds(area, lineHeight);
        int left = speaker.Left - speaker.Width - 6;
        return left < area.Left ? Rectangle.Empty : new(left, speaker.Top, speaker.Width, speaker.Height);
    }
    public static void Draw(Graphics graphics, Rectangle bounds, DisplayTheme theme, bool highlighted)
    {
        if (bounds.IsEmpty) return;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds);
            using (var brush = new SolidBrush(theme.BackgroundColor)) graphics.FillRectangle(brush, bounds);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 44f, bounds.Height / 44f);
            using var pen = new Pen(highlighted ? ColorSafety.Selection(theme) : theme.TextColor, 2.2f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            graphics.DrawLines(pen, [new(9, 10), new(9, 34), new(36, 34)]);
            graphics.DrawLines(pen, [new(13, 27), new(20, 21), new(26, 24), new(35, 13)]);
            using var marker = new SolidBrush(pen.Color);
            foreach (var point in new Point[] { new(13, 27), new(20, 21), new(26, 24), new(35, 13) })
                graphics.FillEllipse(marker, point.X - 1.7f, point.Y - 1.7f, 3.4f, 3.4f);
        }
        finally { graphics.Restore(state); }
    }
}
