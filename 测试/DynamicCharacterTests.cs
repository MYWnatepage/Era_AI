using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;

internal static class DynamicCharacterTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static void Run()
    {
        var state = new InventoryState();
        var characters = new Dictionary<string, string> { ["player"] = "主角", ["companion"] = "同伴" };
        var owners = new Dictionary<string, string>(characters) { ["world"] = "无主" };
        state.BindOwners(owners); state.BindCharacters(characters);
        state.SetScene(new() { Location = "村落集市", PresentCharacters = ["player"], Summary = "主角抵达集市。" });
        state.AddInitial(1, "铜币", "通行的货币。", "普通物品", "", 81, "player", "");
        string before = state.Snapshot();
        var npc = new { op = "create_character", character = "npc_1", name = "罗莎", description = "卖食材的商人，说话爽快，戴一条蓝围巾。", location = "村落集市", reason = "本轮相识并询问货价。" };
        string Reply(object[] people, string[] present, object[]? items = null, object[]? statuses = null) => JsonSerializer.Serialize(new
        {
            story = "商人罗莎介绍了自己，收下铜币，露出手上的擦伤。", turn_id = "meet", base_revision = 0,
            inventory_ops = items ?? [], state_ops = statuses ?? [], character_ops = people,
            scene_update = new { location = "村落集市", present_characters = present, summary = "主角与罗莎交谈。", reason = "首次相识。" }
        });
        var result = InventoryReply.Apply(new(before, "meet", GameTime: "第3天 时间1/8"), Reply([npc], ["player", "npc_1"],
            [new { op = "transfer_item", item_id = 1, from_owner = "player", to_owner = "npc_1", quantity = 1, reason = "付给罗莎一枚铜币。" }],
            [new { op = "create_status", character = "npc_1", name = "手背擦伤", category = "伤势", severity = "轻微", description = "手背有一道浅擦伤。", effects = "", reason = "本轮看见。" }]));
        var saved = InventoryState.Read(result.Snapshot);
        Check(result.RequiresApproval && state.Snapshot() == before && saved.DynamicCharacters["npc_1"].Name == "罗莎" &&
            saved.CharacterLocations["npc_1"] == "村落集市" && saved.Item(1).Quantities["npc_1"] == 1 && saved.Statuses.Single().Character == "npc_1",
            "新人物、归属、伤势及在场登记进入同一待确认快照，原状态不变");
        saved.BindOwners(owners); saved.BindCharacters(characters); saved.BindWorld(new ScenarioDefinition());
        var restored = InventoryState.Read(saved.Snapshot());
        Check(restored.Characters["npc_1"] == "罗莎" && restored.Owners["npc_1"] == "罗莎" && restored.AvailableCharacterId() == "npc_2" &&
            CharacterOverview.Detail(restored, "npc_1", 0)[28].Contains("蓝围巾"),
            "读取和剧本重新绑定保留新人物、归属和完整人物说明，不重建或丢失");
        string Prompt(string player) => PromptBuilder.User(new AiRequest("背景", player, "fixture", "第4天", 1024, 0, "", Inventory: new(restored.Snapshot(), "next")), []);
        using (var current = JsonDocument.Parse(Prompt("问问货价")))
            Check(current.RootElement.GetProperty("program_context").GetProperty("character_cards")[0].GetProperty("description").GetString()!.Contains("蓝围巾"),
                "在场新人物的已确认特征即使短期历史为空仍送入本轮资料");
        restored.SetScene(new() { Location = "营地", PresentCharacters = ["player"], Summary = "主角在营地。" });
        using (var irrelevant = JsonDocument.Parse(Prompt("看看天空")))
            Check(irrelevant.RootElement.GetProperty("program_context").GetProperty("character_cards").GetArrayLength() == 0 &&
                !irrelevant.RootElement.GetProperty("program_context").GetProperty("characters").TryGetProperty("npc_1", out _),
                "离场且未被提及的新人物资料不随每轮发送");
        using (var named = JsonDocument.Parse(Prompt("想起罗莎的蓝围巾")))
            Check(named.RootElement.GetProperty("program_context").GetProperty("character_cards").GetArrayLength() == 1,
                "明确点名场外人物时按需找回资料");
        using (var numbered = JsonDocument.Parse(Prompt("询问npc_1的近况")))
            Check(numbered.RootElement.GetProperty("program_context").GetProperty("character_cards").GetArrayLength() == 1,
                "明确人物编号与姓名同样可按需找回人物资料");
        var corrupted = JsonNode.Parse(saved.Snapshot())!; corrupted["owners"] = null;
        try { InventoryState.Read(corrupted.ToJsonString()); throw new Exception("损坏归属表应拒绝读取"); }
        catch (InvalidOperationException) { Check(true, "新增人物存档的归属登记损坏时明确拒绝，不发生空引用崩溃"); }
        foreach (string invalid in new[] { Reply([npc], ["player"]), Reply([npc], ["player", "npc_1"]).Replace("npc_1", "invented") })
        {
            try { InventoryReply.Apply(new(before, "meet"), invalid); throw new Exception("应拒绝错误人物登记"); }
            catch (InvalidOperationException) { Check(state.Snapshot() == before, "错误人物编号或缺少在场登记时整轮拒绝且不改原状态"); }
        }
        var duplicate = JsonSerializer.Serialize(new { story = "又见商人。", turn_id = "dup", base_revision = restored.Revision,
            inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>(),
            character_ops = new[] { new { op = "create_character", character = "npc_2", name = "罗莎", description = "另一份说明。", location = "村落集市", reason = "试图重复登记。" } } });
        try { InventoryReply.Apply(new(restored.Snapshot(), "dup"), duplicate); throw new Exception("应拒绝同名NPC"); }
        catch (InvalidOperationException) { Check(restored.DynamicCharacters.Count == 1, "再次相遇必须复用原人物，拒绝同名重复建卡"); }
        using var legacyDoc = JsonDocument.Parse(before);
        string legacy = JsonSerializer.Serialize(legacyDoc.RootElement.EnumerateObject().Where(p => p.Name is not ("dynamic_characters" or "next_character_id")).ToDictionary(p => p.Name, p => p.Value.Clone()));
        Check(InventoryState.Read(legacy).DynamicCharacters.Count == 0, "旧存档缺少新增人物字段仍可读取");
        for (int i = 2; i <= 6; i++)
        {
            string id = "npc_" + i;
            restored.Characters[id] = restored.Owners[id] = "商人" + i;
            restored.DynamicCharacters[id] = new() { Name = "商人" + i, Description = "这是第" + i + "位商人。" };
            restored.CharacterLocations[id] = "营地"; restored.Scene!.PresentCharacters.Add(id);
        }
        restored.NextCharacterId = 7;
        using var crowded = JsonDocument.Parse(Prompt("问商人6叫什么"));
        var cards = crowded.RootElement.GetProperty("program_context").GetProperty("character_cards");
        Check(cards.GetArrayLength() == 4 && cards[0].GetProperty("id").GetString() == "npc_6", "多人同场时优先提供明确点名者的人物资料，仍限制总卡数");
    }
}
