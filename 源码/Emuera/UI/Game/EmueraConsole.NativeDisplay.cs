using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    public void ApplyTypography()
    {
        stringMeasure.RefreshTypography();
        userStyle.Fontname = Runtime.Config.Config.FontName;
        defaultStyle.Fontname = Runtime.Config.Config.FontName;
        window.ApplyTypographyLayout();
        foreach (var line in displayLineList)
            foreach (var button in line.Buttons)
            {
                button.bitmapCache?.Dispose();
                button.bitmapCache = null;
            }
        System.Array.Clear(bitmapCacheArray);
    }
    // Apply a complete validated pair atomically. No script input or display lines are modified.
    private int previousFocusRgb = -2;
    public void ApplyDisplayTheme(DisplayTheme theme)
    {
        int focusRgb = ColorSafety.Pack(ColorSafety.Selection(theme));
        bool changed = previousFocusRgb != focusRgb || bgColor.ToArgb() != theme.BackgroundColor.ToArgb() || userStyle.Color.ToArgb() != theme.TextColor.ToArgb();
        if (changed)
        {
            foreach (var line in displayLineList)
                foreach (var button in line.Buttons)
                {
                    button.bitmapCache?.Dispose();
                    button.bitmapCache = null;
                }
            System.Array.Clear(bitmapCacheArray);
        }
        previousFocusRgb = focusRgb;
        bgColor = theme.BackgroundColor;
        userStyle.Color = theme.TextColor;
        defaultStyle.Color = theme.TextColor;
        window.TextBox.BackColor = theme.BackgroundColor;
        window.TextBox.ForeColor = theme.TextColor;
        window.BackColor = theme.BackgroundColor;
        // 设置页每次重建都会重新应用配色；配色未变时不绘制清空后的半成品。
        if (changed) RefreshStrings(true);
    }
}
