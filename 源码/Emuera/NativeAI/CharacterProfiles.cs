#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

// A save-owned copy of scenario card data. Initial health is recorded only by explicit new-game setup.
internal sealed class CharacterProfile
{
    public int Age { get; set; }
    public int HeightCm { get; set; }
    public int WeightKg { get; set; }
    public string InitialHealth { get; set; } = "";
    public string InitialHealthNote { get; set; } = "";
}

internal sealed partial class InventoryState
{
    public Dictionary<string, CharacterProfile> CharacterProfiles { get; set; } = new();

    internal void EnsureCharacterProfile(string character, int age, int heightCm, int weightKg,
        string health, string note, bool newGame)
    {
        if (!Characters.ContainsKey(character)) throw new ArgumentException("人物未在当前剧本中登记。");
        var profile = new CharacterProfile { Age = age, HeightCm = heightCm, WeightKg = weightKg };
        if (newGame) { profile.InitialHealth = health; profile.InitialHealthNote = note; }
        ValidateProfile(profile);
        if (!CharacterProfiles.TryGetValue(character, out var current)) CharacterProfiles.Add(character, profile);
        else if (newGame && current.InitialHealth.Length == 0)
        {
            current.InitialHealth = profile.InitialHealth;
            current.InitialHealthNote = profile.InitialHealthNote;
        }
    }

    private static void ValidateProfile(CharacterProfile profile)
    {
        if (profile == null || profile.Age is < 0 or > 10000 || profile.HeightCm is < 1 or > 10000 ||
            profile.WeightKg is < 1 or > 100000) throw new ArgumentException("人物基础资料无效。");
        Text(profile.InitialHealth, 80, false);
        Text(profile.InitialHealthNote, 1000, false);
    }

    private void ValidateProfiles()
    {
        if (CharacterProfiles == null || CharacterProfiles.Count > 256) throw new ArgumentException("人物基础资料表无效。");
        foreach (var pair in CharacterProfiles)
        {
            if (!Characters.ContainsKey(pair.Key)) throw new ArgumentException("人物基础资料包含未登记的人物。");
            ValidateProfile(pair.Value);
        }
    }

    internal string CharacterHealth(string character)
    {
        var conditions = Statuses.Where(s => s.Character == character).ToArray();
        if (conditions.Length > 0) return string.Join("、", conditions.Select(s => s.Name));
        return CharacterProfiles.TryGetValue(character, out var profile) && profile.InitialHealth.Length > 0
            ? profile.InitialHealth : "暂无状态记录";
    }
}
