using System;
using System.Collections.Generic;
using System.Drawing.Text;

namespace MinorShift.Emuera.NativeDisplay;

internal static class FontNameResolver
{
    private static readonly string[][] Aliases =
    [
        ["SimSun", "宋体"],
        ["SimHei", "黑体"],
        ["KaiTi", "楷体", "KaiTi_GB2312", "楷体_GB2312"],
        ["Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "微软雅黑 UI"]
    ];
    public static string Resolve(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requested.Trim() };
        foreach (var group in Aliases)
            if (Array.Exists(group, name => names.Contains(name)))
                foreach (var name in group) names.Add(name);
        using var fonts = new InstalledFontCollection();
        foreach (var family in fonts.Families)
        {
            if (names.Contains(family.Name)) return family.Name;
            foreach (int language in new[] { 0x409, 0x804, 0x404 })
            {
                try { if (names.Contains(family.GetName(language))) return family.Name; }
                catch (ArgumentException) { }
            }
        }
        return null;
    }
}
