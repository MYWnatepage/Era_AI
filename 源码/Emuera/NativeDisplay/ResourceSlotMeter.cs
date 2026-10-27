#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeDisplay;

internal enum ResourceMeterKind { ActionPoints, Fatigue }
internal enum ResourceMeterBand { Healthy, Caution, Strained, Danger }

// Pure geometry and painting shared by the in-line console shape and bitmap checks.
// Slots are filled by area, so a short walk keeps its fractional action point visible.
internal sealed class ResourceSlotMeter
{
    internal decimal Value { get; }
    internal decimal Maximum { get; }
    internal int Slots { get; }
    internal ResourceMeterKind Kind { get; }
    internal float Height { get; }
    internal float Step { get; }
    internal float Skew { get; }
    internal float Leading { get; }
    internal float Width { get; }
    internal float StrokeWidth { get; }
    internal decimal Ratio => Value / Maximum;
    internal ResourceMeterBand Band => Kind == ResourceMeterKind.ActionPoints
        ? Ratio > .5m ? ResourceMeterBand.Healthy : Ratio > .25m ? ResourceMeterBand.Caution : ResourceMeterBand.Danger
        : Ratio < .25m ? ResourceMeterBand.Healthy : Ratio < .5m ? ResourceMeterBand.Caution : Ratio < .75m ? ResourceMeterBand.Strained : ResourceMeterBand.Danger;
    internal string PlainText => (Kind == ResourceMeterKind.ActionPoints ? "行动点 " : "疲劳 ") +
        Value.ToString("0.###", CultureInfo.InvariantCulture) + "/" + Maximum.ToString("0.###", CultureInfo.InvariantCulture);

    internal ResourceSlotMeter(decimal value, decimal maximum, int slots, ResourceMeterKind kind, float fontSize, float? maximumWidth = null)
    {
        if (maximum <= 0 || maximum > 1000000) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (slots is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(slots));
        if (kind is not (ResourceMeterKind.ActionPoints or ResourceMeterKind.Fatigue)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!float.IsFinite(fontSize) || fontSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (maximumWidth.HasValue && (!float.IsFinite(maximumWidth.Value) || maximumWidth.Value <= 0)) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        Value = Math.Clamp(value, 0, maximum); Maximum = maximum; Slots = slots; Kind = kind;
        Height = Math.Max(12, fontSize * .64f);
        Step = Math.Max(18, fontSize * .94f);
        Skew = Height * .25f;
        Leading = Math.Max(5, fontSize * .28f);
        StrokeWidth = Math.Max(1, fontSize / 18f);
        Width = Leading + Step * Slots + Skew + StrokeWidth + 2;
        if (maximumWidth is float available && Width > available)
        {
            // Interpolate to the minimum readable geometry. Every dimension is
            // linear in this weight, so the resulting total fits the exact budget.
            float minimumWidth = Slots * 12 + 8;
            float weight = Math.Clamp((available - minimumWidth) / (Width - minimumWidth), 0, 1);
            Step = 12 + (Step - 12) * weight;
            Height = 8 + (Height - 8) * weight;
            Leading = 3 + (Leading - 3) * weight;
            StrokeWidth = 1 + (StrokeWidth - 1) * weight;
            Skew = Height * .25f;
            Width = Leading + Step * Slots + Skew + StrokeWidth + 2;
        }
    }

    internal float FillAt(int slot)
    {
        if (slot < 0 || slot >= Slots) throw new ArgumentOutOfRangeException(nameof(slot));
        return (float)Math.Clamp(Ratio * Slots - slot, 0, 1);
    }

    internal PointF[] PolygonAt(int slot, float pointX, float top, float fraction = 1)
    {
        if (slot < 0 || slot >= Slots) throw new ArgumentOutOfRangeException(nameof(slot));
        fraction = Math.Clamp(fraction, 0, 1);
        float x = pointX + Leading + slot * Step + 1, width = (Step - 5) * fraction;
        return [new(x + Skew, top), new(x + width + Skew, top), new(x + width, top + Height), new(x, top + Height)];
    }

    internal Color ColorFor(DisplayTheme theme)
    {
        theme = theme.Safe();
        bool dark = ColorSafety.Luminance(theme.BackgroundColor) < .19;
        Color seed = (Band, dark) switch
        {
            (ResourceMeterBand.Healthy, true) => Color.FromArgb(99, 220, 135),
            (ResourceMeterBand.Caution, true) => Color.FromArgb(241, 207, 83),
            (ResourceMeterBand.Strained, true) => Color.FromArgb(255, 160, 76),
            (ResourceMeterBand.Danger, true) => Color.FromArgb(246, 105, 111),
            (ResourceMeterBand.Healthy, false) => Color.FromArgb(26, 112, 62),
            (ResourceMeterBand.Caution, false) => Color.FromArgb(126, 91, 0),
            (ResourceMeterBand.Strained, false) => Color.FromArgb(165, 73, 0),
            _ => Color.FromArgb(176, 36, 48),
        };
        if (ColorSafety.Contrast(seed, theme.BackgroundColor) >= ColorSafety.MinimumContrast) return seed;
        // Keep the hue as far as possible, even on a custom mid-tone background.
        Color target = ColorSafety.ReadableFallback(theme.BackgroundColor);
        for (int i = 1; i <= 100; i++)
        {
            float weight = i / 100f;
            Color next = Color.FromArgb((int)Math.Round(seed.R + (target.R - seed.R) * weight),
                (int)Math.Round(seed.G + (target.G - seed.G) * weight),
                (int)Math.Round(seed.B + (target.B - seed.B) * weight));
            if (ColorSafety.Contrast(next, theme.BackgroundColor) >= ColorSafety.MinimumContrast) return next;
        }
        return target;
    }

    internal void Draw(Graphics graph, float pointX, float textTop, float textCenter, DisplayTheme theme)
    {
        using var pen = new Pen(ColorFor(theme), StrokeWidth);
        using var brush = new SolidBrush(pen.Color);
        GraphicsState state = graph.Save();
        try
        {
            graph.SmoothingMode = SmoothingMode.AntiAlias;
            float top = textTop + textCenter - Height / 2;
            for (int slot = 0; slot < Slots; slot++)
            {
                float fill = FillAt(slot);
                if (fill > 0) graph.FillPolygon(brush, PolygonAt(slot, pointX, top, fill));
                // The outline keeps the unfilled portion of a partial slot visible.
                if (fill < 1) graph.DrawPolygon(pen, PolygonAt(slot, pointX, top));
            }
        }
        finally { graph.Restore(state); }
    }
}

// Alignment uses visible ink rather than font ascent, matching the existing time slots.
internal static class SlotTextAlignment
{
    private static readonly Dictionary<(string, float, FontStyle, bool, float, float), float> Centers = new();
    internal static float Center(Graphics target, Font font, bool graphicsMode)
    {
        var key = (font.Name, font.Size, font.Style, graphicsMode, target.DpiX, target.DpiY);
        if (Centers.TryGetValue(key, out float value)) return value;
        int size = Math.Max(32, (int)Math.Ceiling(font.GetHeight(target) * 2));
        using var sample = new Bitmap(size * 4, size);
        sample.SetResolution(target.DpiX, target.DpiY);
        using (var graph = Graphics.FromImage(sample))
        {
            graph.Clear(Color.Black);
            if (graphicsMode) graph.DrawString("清晨", font, Brushes.White, PointF.Empty);
            else TextRenderer.DrawText(graph, "清晨", font, Point.Empty, Color.White,
                TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        }
        int top = size, bottom = -1;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < sample.Width; x++)
        {
            var pixel = sample.GetPixel(x, y);
            if (pixel.R + pixel.G + pixel.B > 48) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        }
        value = bottom >= top ? (top + bottom) / 2f : font.GetHeight(target) / 2;
        if (Centers.Count >= 64) Centers.Clear();
        Centers[key] = value;
        return value;
    }
}
