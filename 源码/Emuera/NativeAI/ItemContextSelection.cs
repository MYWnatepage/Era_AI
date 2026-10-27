#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

// Explicit context selection lasts for one request. Selecting an item never changes its quantity or owner.
internal static class ItemContextSelection
{
    internal static int[] Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        if (csv.Split(',').Any(token => string.IsNullOrWhiteSpace(token)))
            throw new ArgumentException("关联物品编号无效。");
        return InventoryGrid.SelectedIds(string.Join(",", csv.Split(',').Select(token => token.Split(':')[0].Trim())));
    }

    internal static Dictionary<int, int> Amounts(string csv)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(csv)) return result;
        Parse(csv);
        foreach (string token in csv.Split(','))
        {
            var parts = token.Trim().Split(':');
            if (parts.Length > 2 || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int _) || parts[1].StartsWith("0"))))
                throw new ArgumentException("关联物品数量格式无效。");
            int count = parts.Length == 1 ? 1 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (count < 1) throw new ArgumentException("关联数量必须大于零。");
            result[id] = count;
        }
        return result;
    }
    internal static int Available(InventoryState state, InventoryItem item) => (int)Math.Min(int.MaxValue,
        item.Quantities.Where(pair => state.Characters.ContainsKey(pair.Key) && (pair.Key == "player" ||
            state.Scene?.PresentCharacters.Contains(pair.Key) == true)).Sum(pair => (long)pair.Value));
    internal static Dictionary<int,int> ValidateAmounts(InventoryState state, string csv)
    {
        var amounts = Amounts(csv); Select(state, amounts.Keys);
        if (amounts.Any(pair => pair.Value > Available(state, state.Item(pair.Key))))
            throw new InvalidOperationException("关联数量超过当前可用库存。");
        return amounts;
    }
    internal static string Adjust(InventoryState state, string csv, int id, int delta)
    {
        if (delta is not (1 or -1 or 10 or -10)) throw new ArgumentException("数量调整无效。");
        var amounts = ValidateAmounts(state, csv); Select(state, [id]);
        int current = amounts.GetValueOrDefault(id);
        int next = (int)Math.Clamp((long)current + delta, 0, Available(state, state.Item(id)));
        if (next == 0) amounts.Remove(id);
        else { if (!amounts.ContainsKey(id) && amounts.Count >= 16) throw new ArgumentException("每次最多关联16种物品。"); amounts[id] = next; }
        return string.Join(",", amounts.Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture)));
    }

    internal static int[] Select(InventoryState state, IEnumerable<int> ids)
    {
        if (ids == null) throw new ArgumentException("关联物品编号无效。");
        var selected = ids.Distinct().ToArray();
        if (selected.Length > 16) throw new ArgumentException("每次最多关联16种物品。");
        foreach (int id in selected)
        {
            if (id < 1 || id == int.MaxValue) throw new ArgumentException("关联物品编号无效。");
            if (!InventoryGrid.Selectable(state, state.Item(id)))
                throw new InvalidOperationException("只能关联自己或在场人物仍持有的物品。");
        }
        return selected;
    }

    internal static string Toggle(InventoryState state, string csv, int id)
    {
        var selected = Select(state, Parse(csv)).ToList();
        if (!selected.Remove(id))
        {
            Select(state, [id]);
            if (selected.Count == 16) throw new ArgumentException("每次最多关联16种物品。");
            selected.Add(id);
        }
        return string.Join(",", selected.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

    internal static InventoryExchange Attach(InventoryExchange exchange, IEnumerable<int> ids) =>
        exchange with { SelectedItems = Select(InventoryState.Read(exchange.Snapshot), ids) };

    internal static bool Matches(InventoryExchange prepared, string snapshot, string scenarioId) =>
        prepared.ScenarioId == scenarioId && prepared.Snapshot == snapshot;
}
