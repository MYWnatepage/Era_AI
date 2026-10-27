#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

// Importance controls portrait detail, never the fidelity of inventory, injury or location records.
internal sealed partial class InventoryState
{
    public Dictionary<string, string> CharacterImportance { get; set; } = new();

    internal string ImportanceOf(string character) => CharacterImportance.GetValueOrDefault(character,
        character == "player" ? "major" : "minor");
    internal string ImportanceLabel(string character) => ImportanceOf(character) == "major" ? "主要人物" : "次要人物";

    internal void BindCharacterImportance(ScenarioDefinition scenario)
    {
        foreach (string character in Characters.Keys)
        {
            if (DynamicCharacters.ContainsKey(character)) CharacterImportance.TryAdd(character, "minor");
            else CharacterImportance[character] = character == "player" || scenario.MajorCharacters.Contains(character, StringComparer.Ordinal)
                ? "major" : "minor";
        }
        ValidateCharacterImportance();
    }

    private void ValidateCharacterImportance()
    {
        if (CharacterImportance == null || CharacterImportance.Count > 256)
            throw new ArgumentException("人物重要程度登记无效。");
        foreach (var (character, importance) in CharacterImportance)
            if (!Characters.ContainsKey(character) || importance is not ("major" or "minor"))
                throw new ArgumentException("人物重要程度包含未登记人物或无效层级。");
    }

    internal void SetCharacterImportance(string character, string importance)
    {
        if (!Characters.ContainsKey(character)) throw new ArgumentException("请选择已登记的人物。");
        if (!DynamicCharacters.ContainsKey(character)) throw new ArgumentException("初始人物的重要程度由剧本人物卡设定。");
        if (importance is not ("major" or "minor")) throw new ArgumentException("人物只能设为主要人物或次要人物。");
        if (ImportanceOf(character) == importance) return;
        if (Revision >= int.MaxValue - 1) throw new ArgumentException("当前状态版本已达到上限。");
        CharacterImportance[character] = importance;
        Revision = checked(Revision + 1);
    }

    internal object[] CharacterPromptCards(IReadOnlySet<string> participants, string player, string? targetCharacter = null)
    {
        return DynamicCharacters.Where(p => participants.Contains(p.Key))
            .OrderByDescending(p => player.Contains(p.Value.Name, StringComparison.Ordinal) || player.Contains(p.Key, StringComparison.Ordinal))
            .ThenByDescending(p => targetCharacter == p.Key)
            .ThenByDescending(p => ImportanceOf(p.Key) == "major").Take(4)
            .Select(p =>
            {
                int limit = ImportanceOf(p.Key) == "major" ? 600 : 120;
                string text = p.Value.Description.ReplaceLineEndings(" ").Trim();
                return (object)new { id = p.Key, name = p.Value.Name, importance = ImportanceOf(p.Key),
                    description = text.Length <= limit ? text : text[..(limit - 1)] + "…" };
            }).ToArray();
    }
}
