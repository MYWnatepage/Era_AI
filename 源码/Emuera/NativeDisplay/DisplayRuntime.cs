using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MinorShift.Emuera.GameView;

namespace MinorShift.Emuera.NativeDisplay;

internal static class DisplayRuntime
{
    private static readonly DisplayThemeStore Store = new(Path.Combine(Program.ExeDir, "配置"));
    public static DisplayTheme Current { get; private set; }
    public static bool Editing { get; private set; }
    public static void Apply(EmueraConsole console, DisplayTheme fallback)
    {
        TypographyRuntime.Ensure(console);
        Current ??= Store.Load(fallback);
        Preview(console, Current);
    }
    private static void Preview(EmueraConsole console, DisplayTheme theme)
    {
        if (!theme.IsValid) throw new ArgumentException("配色未达到安全对比度。");
        Current = theme;
        console.ApplyDisplayTheme(theme);
    }
    public static void Configure(EmueraConsole console, int selected)
    {
        if (Editing) return;
        var original = Current;
        Editing = true;
        var editor = new GameColorEditor(original, selected, theme => Preview(console, theme), theme => Store.Save(theme), saved =>
        {
            if (!saved) Preview(console, original);
            console.Window.CloseDisplayEditor();
            Editing = false;
        });
        console.Window.ShowDisplayEditor(editor);
    }
    public static Color ResolveText(Color original, bool selected)
    {
        if (Current == null) return original;
        return selected ? ColorSafety.Selection(Current) : Current.TextColor;
    }
}
