#nullable enable
using System;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

// Read-only presentation of registered people and the conditions already in this save.
// Empty conditions imply health only when this save has an explicit new-game health baseline.
internal static class CharacterOverview
{
    internal static string[] Page(InventoryState state, int page)
    {
        var people = state.Characters.OrderBy(pair => pair.Key == "player" ? 0 : state.ImportanceOf(pair.Key) == "major" ? 1 : 2)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        int pages = Math.Max(1, (people.Length + 9) / 10);
        page = Math.Clamp(page, 0, pages - 1);
        var values = Enumerable.Repeat("", 24).ToArray();
        int row = 0;
        foreach (var person in people.Skip(page * 10).Take(10))
        {
            string profile = state.CharacterProfiles.TryGetValue(person.Key, out var data)
                ? $"{data.Age}岁 / {data.HeightCm} cm / {data.WeightKg} kg / " : "";
            values[row] = $"[{row + 1}] {person.Value} / {state.ImportanceLabel(person.Key)}    {profile}{state.CharacterHealth(person.Key)} / {Location(state, person.Key)}";
            values[12 + row] = person.Key;
            row++;
        }
        values[10] = pages.ToString();
        values[11] = people.Length.ToString();
        return values;
    }

    internal static string[] Detail(InventoryState state, string character, int page)
    {
        if (!state.Characters.TryGetValue(character, out string? name))
            throw new ArgumentException("请选择已登记的人物。");
        var statuses = state.Statuses.Where(status => status.Character == character).OrderBy(status => status.Id).ToArray();
        int pages = Math.Max(1, (statuses.Length + 9) / 10);
        page = Math.Clamp(page, 0, pages - 1);
        var values = Enumerable.Repeat("", 32).ToArray();
        values[0] = name;
        values[1] = Location(state, character);
        values[2] = statuses.Length.ToString();
        values[3] = pages.ToString();
        if (state.CharacterProfiles.TryGetValue(character, out var profile))
        {
            values[24] = profile.Age + " 岁";
            values[25] = profile.HeightCm + " cm";
            values[26] = profile.WeightKg + " kg";
            if (statuses.Length == 0) values[28] = profile.InitialHealthNote;
        }
        else { values[24] = values[25] = values[26] = "未记录"; }
        if (state.DynamicCharacters.TryGetValue(character, out var person)) values[28] = person.Description;
        values[27] = state.CharacterHealth(character);
        values[29] = state.ImportanceLabel(character);
        values[30] = state.ImportanceOf(character);
        values[31] = state.DynamicCharacters.ContainsKey(character) ? "1" : "0";
        int row = 0;
        foreach (var status in statuses.Skip(page * 10).Take(10))
        {
            values[4 + row] = $"[{row + 1}] {status.Name} / {status.Severity}";
            values[14 + row] = status.Id.ToString();
            row++;
        }
        return values;
    }

    private static string Location(InventoryState state, string character)
    {
        if (state.CharacterLocations.TryGetValue(character, out string? location)) return location;
        return state.Scene?.PresentCharacters.Contains(character) == true ? state.Scene.Location : "未知";
    }
}
