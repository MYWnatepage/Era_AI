#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MinorShift.Emuera.NativeAI;

internal sealed record InventoryGridCell(int Id, string Label, bool Selected);
internal sealed record InventoryGridPage(int Page, int Pages, int Total, int Columns, int CellWidth,
    IReadOnlyList<InventoryGridCell> Cells, string SelectedSummary);

// The grid carries only presentation and explicit selection. It never consumes or transfers items.
internal static class InventoryGrid
{
    internal static InventoryGridPage Build(InventoryState state, int page, IEnumerable<int>? selectedIds,
        int columns, int cellWidth = 18, bool selectableOnly = false)
    {
        columns = Math.Clamp(columns, 2, 4);
        cellWidth = Math.Clamp(cellWidth, 10, 40);
        var selected = (selectedIds ?? Array.Empty<int>()).ToHashSet();
        var items = state.Items.Where(item => !selectableOnly || Selectable(state, item)).OrderBy(item => item.Id).ToArray();
        int count = columns * 4;
        int pages = Math.Max(1, (items.Length + count - 1) / count);
        page = Math.Clamp(page, 0, pages - 1);
        var cells = items.Skip(page * count).Take(count)
            .Select(item => new InventoryGridCell(item.Id, Label(item.Name, cellWidth - 2), selected.Contains(item.Id)))
            .ToArray();
        string summary = string.Join("、", items.Where(item => selected.Contains(item.Id)).Select(item => Label(item.Name, 80)));
        return new(page, pages, items.Length, columns, cellWidth, cells, summary);
    }

    internal static int[] SelectedIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var tokens = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > InventoryState.MaxItems) throw new ArgumentException("关联物品过多。");
        var result = new List<int>();
        foreach (string token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 1 || id == int.MaxValue)
                throw new ArgumentException("关联物品编号无效。");
            if (!result.Contains(id)) result.Add(id);
            if (result.Count > 16) throw new ArgumentException("每次最多关联16种物品。");
        }
        return result.ToArray();
    }

    internal static bool Selectable(InventoryState state, InventoryItem item) => item.Quantities.Any(pair =>
        pair.Value > 0 && state.Characters.ContainsKey(pair.Key) && (pair.Key == "player" ||
            (state.Scene?.PresentCharacters.Contains(pair.Key, StringComparer.Ordinal) ?? false)));

    internal static int DisplayWidth(string value)
    {
        int width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            int n = rune.Value;
            width += n >= 0x1100 && (n <= 0x115F || n is 0x2329 or 0x232A ||
                n >= 0x2E80 && n <= 0xA4CF && n != 0x303F || n >= 0xAC00 && n <= 0xD7A3 ||
                n >= 0xF900 && n <= 0xFAFF || n >= 0xFE10 && n <= 0xFE19 ||
                n >= 0xFE30 && n <= 0xFE6F || n >= 0xFF00 && n <= 0xFF60 ||
                n >= 0xFFE0 && n <= 0xFFE6 || n >= 0x1F300 && n <= 0x1FAFF || n >= 0x20000) ? 2 : 1;
        }
        return width;
    }

    internal static string Label(string name, int width)
    {
        string singleLine = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (DisplayWidth(singleLine) <= width) return singleLine;
        var builder = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(singleLine);
        int used = 0;
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int next = DisplayWidth(element);
            if (used + next > Math.Max(0, width - 1)) break;
            builder.Append(element); used += next;
        }
        return builder.Append('…').ToString();
    }

    // Only exact shipped defaults are upgraded. User edits and generated item details remain untouched.
    internal static bool CleanLegacy(InventoryState state, InventoryTextMigrations rules)
    {
        rules.Validate();
        bool changed = false;
        foreach (var item in state.Items)
        {
            var initial = rules.Items.FirstOrDefault(rule => rule.ItemId == item.Id && rule.Name == item.Name);
            if (initial == null) continue;
            if (item.Description == initial.OldDescription && item.Description != initial.Description)
            { item.Description = initial.Description; changed = true; }
            if (item.Attributes == initial.OldAttributes && item.Attributes != initial.Attributes)
            { item.Attributes = initial.Attributes; changed = true; }
        }
        return changed;
    }

}
