using System;
using System.Drawing;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.NativeDisplay;

// Vector slots stay crisp at every font size; no dependency on tiny square font glyphs.
internal sealed class TimeSlotsPart : ConsoleShapePart
{
    private readonly int used, total;
    private readonly float height, step, skew, leading;
    private float center;
    // Align to the actual visible font ink, including the padding used by the text renderer.
    internal static float TextCenter(Graphics target, Font font, TextDrawingMode mode) =>
        SlotTextAlignment.Center(target, font, mode == TextDrawingMode.GRAPHICS);
    internal TimeSlotsPart(int used, int total)
    {
        this.total = Math.Clamp(total, 1, 32);
        this.used = Math.Clamp(used, 0, this.total);
        height = Math.Max(12, Config.FontSize * .64f);
        step = Math.Max(18, Config.FontSize * .94f);
        skew = height * .25f;
        leading = Math.Max(5, Config.FontSize * .28f);
        WidthF = leading + step * this.total + skew + 2;
        Text = new string('■', this.used) + new string('□', this.total - this.used);
    }
    public override void SetWidth(StringMeasure sm, float subPixel)
    {
        Width = (int)(WidthF + subPixel); XsubPixel = WidthF + subPixel - Width;
    }
    public override void DrawTo(Graphics graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
    {
        var color = DisplayRuntime.Current?.TextColor ?? Config.ForeColor;
        using var pen = new Pen(color, Math.Max(1, Config.FontSize / 18f));
        using var brush = new SolidBrush(color);
        center = TextCenter(graph, Config.DefaultFont, mode);
        float y = pointY + center - height / 2;
        for (int i = 0; i < total; i++)
        {
            float x = PointX + leading + i * step + 1, w = step - 5;
            PointF[] polygon = [new(x + skew, y), new(x + w + skew, y), new(x + w, y + height), new(x, y + height)];
            if (i < used) graph.FillPolygon(brush, polygon);
            else graph.DrawPolygon(pen, polygon);
        }
    }
}
