using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class SceneContextTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action operation, string message)
    {
        try { operation(); } catch (InvalidOperationException) { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }
    internal static void Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["companion"] = "同伴" });
        state.AddInitial(1, "布条", "干净的布条。", "消耗品", "", 2, "player", "");
        state.SetScene(new() { Location = "林中营地", PresentCharacters = ["player", "companion"], Summary = "两人在篝火旁休息。" });
        string original = state.Snapshot();
        using var serialized = JsonDocument.Parse(original);
        string legacy = JsonSerializer.Serialize(serialized.RootElement.EnumerateObject().Where(p => p.Name is not ("scene" or "context_item_ids"))
            .ToDictionary(p => p.Name, p => p.Value.Clone()));
        var migrated = InventoryState.Read(legacy);
        Check(migrated.Scene == null && migrated.ContextItemIds.Count == 0 && migrated.Quantity(1) == 2 && migrated.Revision == 0,
            "旧存档缺少场景与物品焦点时保持未知，保留物品与版本，不把开场强塞成当前地点");
        using var legacyPrompt = JsonDocument.Parse(PromptBuilder.User(new AiRequest("初始背景", "你好", "fixture", "第1天", 1024, 0, "", Inventory: new(legacy, "legacy")), []));
        Check(legacyPrompt.RootElement.GetProperty("program_context").GetProperty("scene").ValueKind == JsonValueKind.Null &&
            legacyPrompt.RootElement.GetProperty("program_context").TryGetProperty("scene_note", out _), "旧档向模型明确说明场景未知，避免重放初始地点");

        object Scene(string location = "林中营地", string[]? present = null, string summary = "你在营地清洗伤口。") =>
            new { location, present_characters = present ?? ["player", "companion"], summary, reason = "本轮在营地清洗并包扎伤口。" };
        object[] consume = [new { op = "change_quantity", item_id = 1, owner = "player", delta = -1, reason = "取用布条" }];
        object[] wound = [new { op = "create_status", character = "player", name = "手指浅伤", category = "伤势", severity = "轻微",
            description = "手指划伤后已用布条包扎，尚未痊愈。", effects = "按压疼痛", reason = "本轮清洗后包扎" }];
        string Reply(object? scene = null, object[]? items = null, object[]? statuses = null, string turn = "scene-turn", int revision = 0)
        {
            var data = new Dictionary<string, object?> { ["story"] = "你留在营地，清洗伤口后用布条包扎。", ["turn_id"] = turn,
                ["base_revision"] = revision, ["inventory_ops"] = items ?? [], ["state_ops"] = statuses ?? [] };
            if (scene != null) data["scene_update"] = scene;
            return JsonSerializer.Serialize(data);
        }
        var exchange = new InventoryExchange(original, "scene-turn", GameTime: "第2天 时间3/8");
        var proposal = InventoryReply.Apply(exchange, Reply(Scene(), consume, wound));
        var candidate = InventoryState.Read(proposal.Snapshot);
        Check(proposal.RequiresApproval && proposal.Changes.Contains("布条") && proposal.Rationale.Contains("本轮清洗后包扎"), "同地点物品与伤势变化出现在待确认预览中");
        Check(candidate.Scene!.Location == "林中营地" && candidate.Scene.PresentCharacters.SequenceEqual(["player", "companion"]) && candidate.Quantity(1) == 1 &&
            candidate.Statuses.Count == 1 && candidate.Revision == 1 && candidate.ContextItemIds.SequenceEqual([1]) && state.Snapshot() == original,
            "场景、物品消耗与人物伤势仅生成同一候选快照，尚未确认的原状态不变");
        var restored = InventoryState.Read(candidate.Snapshot());
        Check(restored.Snapshot() == candidate.Snapshot() && InventoryState.Read(original).Scene!.Location == "林中营地", "保存还原保留当前场景、人物状态和物品焦点，旧快照不受后续回合影响");
        var noUpdate = InventoryState.Read(InventoryReply.Apply(new(candidate.Snapshot(), "no-scene"), Reply(turn: "no-scene", revision: 1)).Snapshot);
        Check(noUpdate.Scene!.Location == "林中营地" && noUpdate.Scene.Summary == candidate.Scene.Summary && noUpdate.ContextItemIds.SequenceEqual([1]), "省略场景更新保留当前地点与最近已提交物品焦点");
        var summaryOnly = InventoryReply.Apply(exchange, Reply(Scene("林中营地", ["companion", "player"], "篝火旁的交谈继续。")));
        Check(!summaryOnly.RequiresApproval && InventoryState.Read(summaryOnly.Snapshot).Scene!.Summary == "篝火旁的交谈继续。", "单独刷新事件摘要与调整在场人物顺序不增加手动确认");

        var positioned = InventoryState.Read(original);
        positioned.PlayerPosition = new() { X = 41, Z = -19, Location = "林中营地" };
        positioned.MovementRemainder = .643003549m;
        string positionedBefore = positioned.Snapshot();
        var positionedExchange = new InventoryExchange(positionedBefore, "scene-turn");
        foreach (var bypass in new object[]
        {
            Scene("溪边", ["player"], "你沿路回到溪边。"),
            new { location = "林中营地", present_characters = new[] { "companion" }, summary = "主角离开，同行者留在原处。", reason = "绕过路线",
                character_locations = new Dictionary<string, string> { ["player"] = "溪边" } },
            new { location = "林中营地", present_characters = new[] { "player", "companion" }, summary = "仍在营地，却写入另一个主角所在地。", reason = "冲突位置",
                character_locations = new Dictionary<string, string> { ["player"] = "溪边" } },
            Scene("林中营地", ["companion"], "主角走到别处。"),
        })
        {
            try
            {
                InventoryReply.Apply(positionedExchange, Reply(bypass, consume, wound));
                throw new Exception("未拒绝文字移动绕过地图结算");
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("移动") && error.Message.Contains("地图"), "文字移动及移除主角均明确提示通过地图路线结算");
            }
            Check(positioned.Snapshot() == positionedBefore, "被拒文字移动整轮原子回滚，坐标、行动点余量、物品与伤势不变");
        }
        var pointPreserved = InventoryState.Read(InventoryReply.Apply(positionedExchange, Reply(Scene(summary: "你仍在原处整理布条。"))).Snapshot);
        Check(pointPreserved.PlayerPosition is { X: 41, Z: -19 } && pointPreserved.MovementRemainder == .643003549m,
            "合法同地摘要不清除地图精确坐标或小数行动点");

        var unknownPosition = new InventoryState(); unknownPosition.BindCharacters(state.Characters);
        Reject(() => InventoryReply.Apply(new(unknownPosition.Snapshot(), "scene-turn"), Reply(Scene())),
            "完全未知位置的旧档不能由模型任意认定开场地点");
        unknownPosition.CharacterLocations["player"] = "林中营地";
        var confirmedOldLocation = InventoryState.Read(InventoryReply.Apply(new(unknownPosition.Snapshot(), "scene-turn"),
            Reply(Scene(present: ["player"], summary: "继续在已经登记的营地交谈。"))).Snapshot);
        Check(confirmedOldLocation.Scene?.Location == "林中营地" && confirmedOldLocation.CharacterLocations["player"] == "林中营地",
            "旧档可确认已登记主角所在地，不从初始资料猜测新位置");

        object[] invalidScenes = [Scene(present: ["player", "unknown"]), Scene(present: ["player", "player"]), Scene(location: ""),
            Scene(summary: new string('长', 601)), new { location = "溪边", present_characters = new[] { "player" }, summary = "正常", reason = "到达", day = 99 }];
        foreach (var invalid in invalidScenes)
        {
            Reject(() => InventoryReply.Apply(exchange, Reply(invalid, consume, wound)), "非法场景使同轮物品、伤势与场景整体拒绝");
            Check(state.Snapshot() == original, "失败结算不部分消耗物品或新增伤势");
        }
        Reject(() => InventoryReply.Apply(exchange, Reply(Scene(), [new { op = "change_quantity", item_id = 1, owner = "player", delta = -3, reason = "不足" }], wound)),
            "库存不足时已提出的场景移动也不生效");

        var action = new ScenarioAction { Sides = 6, ItemId = 1, Owner = "player", Summary = "D6={roll} 收获{quantity}", Story = "你回到营地。", Instruction = "结果已确定，操作列表为空。",
            EndScene = new() { Location = "林中营地", PresentCharacters = ["player", "companion"], Summary = "外出结束，两人在营地相见。" },
            Outcomes = [new() { Min = 1, Max = 6, Quantity = 1, Tier = "完成", Event = "返回" }] }.Resolve(4);
        var controlled = new InventoryExchange(candidate.Snapshot(), "controlled", action);
        Reject(() => InventoryReply.Apply(controlled, Reply(Scene("林中营地", ["player", "companion"]), turn: "controlled", revision: 1)),
            "程序行动拒绝模型额外场景更新，即使地点看似合理也不允许越过固定结果");
        var resolved = InventoryReply.Apply(controlled, Reply(turn: "controlled", revision: 1));
        var controlledState = InventoryState.Read(resolved.Snapshot);
        Check(!resolved.RequiresApproval && controlledState.Scene!.Location == "林中营地" && controlledState.Quantity(1) == 2 && controlledState.Statuses.Count == 1,
            "程序行动按登记结束场景与固定收获自动结算，不覆盖已有伤势");
    }
}
