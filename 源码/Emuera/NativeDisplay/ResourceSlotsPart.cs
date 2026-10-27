using System;
using System.Drawing;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.NativeDisplay;

// Appended as a shape, never as a button: status bars have no hover or selection focus.
internal sealed class ResourceSlotsPart : ConsoleShapePart
{
    internal ResourceSlotMeter Meter { get; }
    internal ResourceSlotsPart(decimal value, decimal maximum, int slots, ResourceMeterKind kind)
    {
        float available = Math.Max(slots * 12 + 8, Config.DrawableWidth - Math.Max(70, Config.FontSize * 4) - 24);
        Meter = new(value, maximum, slots, kind, Config.FontSize, available);
        WidthF = Meter.Width;
        Text = AltText = Meter.PlainText;
    }
    public override void SetWidth(StringMeasure sm, float subPixel)
    {
        Width = (int)(WidthF + subPixel); XsubPixel = WidthF + subPixel - Width;
    }
    public override void DrawTo(Graphics graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
    {
        var theme = DisplayRuntime.Current ?? new DisplayTheme(ColorSafety.Pack(Config.ForeColor), ColorSafety.Pack(Config.BackColor));
        Meter.Draw(graph, PointX, pointY, TimeSlotsPart.TextCenter(graph, Config.DefaultFont, mode), theme);
    }
}
