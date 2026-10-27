#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

internal sealed class InventoryTextMigration
{
    [JsonRequired] public int ItemId { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string OldDescription { get; set; } = "";
    [JsonRequired] public string Description { get; set; } = "";
    [JsonRequired] public string OldAttributes { get; set; } = "";
    [JsonRequired] public string Attributes { get; set; } = "";
}

// Migration data is supplied by a scenario package. The framework knows no scenario item identities.
internal sealed class InventoryTextMigrations
{
    [JsonRequired] public int FormatVersion { get; set; } = 1;
    [JsonRequired] public List<InventoryTextMigration> Items { get; set; } = [];

    internal void Validate()
    {
        if (FormatVersion != 1 || Items == null || Items.Count > InventoryState.MaxItems ||
            Items.Any(rule => rule == null || rule.ItemId < 1 || rule.ItemId == int.MaxValue) ||
            Items.Select(rule => rule.ItemId).Distinct().Count() != Items.Count)
            throw new ArgumentException("物品说明迁移表无效。");
        foreach (var rule in Items)
        {
            InventoryState.Text(rule.Name, 80, true);
            InventoryState.Text(rule.OldDescription, 2000, true); InventoryState.Text(rule.Description, 2000, true);
            InventoryState.Text(rule.OldAttributes, 1000, false); InventoryState.Text(rule.Attributes, 1000, false);
        }
    }

    internal static InventoryTextMigrations Load(string scenarioDirectory, string relativeFile)
    {
        if (string.IsNullOrEmpty(relativeFile)) return new();
        if (Path.IsPathRooted(relativeFile)) throw new ArgumentException("物品说明迁移表必须使用剧本内的相对路径。");
        string root = Path.GetFullPath(scenarioDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativeFile));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("物品说明迁移表不存在或超出本剧本目录。");
        for (string? part = path; part != null && part.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("物品说明迁移表路径不能经过链接目录或链接文件。");
        if (new FileInfo(path).Length > 256_000) throw new ArgumentException("物品说明迁移表过大。");
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        InventoryReply.RejectDuplicateProperties(document.RootElement);
        var migrations = JsonSerializer.Deserialize<InventoryTextMigrations>(json, InventoryState.Json) ?? throw new JsonException();
        migrations.Validate();
        return migrations;
    }
}
