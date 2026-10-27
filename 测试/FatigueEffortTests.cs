using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class FatigueEffortTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState(); state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.AddInitial(2, "食材", "做饭用的食材。", "消耗品", "", 10, "player", "");
        string Reply(InventoryState current, string turn, string effort, object[]? statuses = null) => JsonSerializer.Serialize(new
        { story = "你处理完手边的事，抬头看看四周。", turn_id = turn, base_revision = current.Revision,
            inventory_ops = Array.Empty<object>(), state_ops = statuses ?? [], effort });
        string before = state.Snapshot();
        var proposal = InventoryReply.Apply(new(before, "heavy", Scenario: scenario), Reply(state, "heavy", "heavy"));
        var worked = InventoryState.Read(proposal.Snapshot);
        Check(proposal.RequiresApproval && worked.FatiguePoints == 20 && state.Snapshot() == before,
            "体力活动只生成待确认疲劳点，原状态不变，未达阈值也会提示点数变化");
        var second = InventoryReply.Apply(new(worked.Snapshot(), "moderate", Scenario: scenario), Reply(worked, "moderate", "moderate"));
        var tired = InventoryState.Read(second.Snapshot);
        Check(tired.FatiguePoints == 30 && tired.Statuses.Count(s => s.Character == "player" && s.Category == "疲劳") == 1 &&
            tired.Statuses.Single(s => s.Category == "疲劳").Severity == "轻微", "累积活动达到阈值才派生单一疲劳状态");
        var none = InventoryState.Read(InventoryReply.Apply(new(tired.Snapshot(), "none", Scenario: scenario), Reply(tired, "none", "none")).Snapshot);
        Check(none.FatiguePoints == 30 && none.Statuses.Single(s => s.Category == "疲劳").Id == tired.Statuses.Single(s => s.Category == "疲劳").Id,
            "交谈与否定表达使用none时不增减疲劳，也不重建状态编号");
        var rules = ScenarioFixture.Ella; rules.FatigueRules.LightEffortCost = 3;
        var custom = InventoryState.Read(InventoryReply.Apply(new(state.Snapshot(), "custom", Scenario: rules), Reply(state, "custom", "light")).Snapshot);
        Check(custom.FatiguePoints == 3, "活动强度使用当前剧本配置，非写死的AI点数");
        foreach (var invalid in new[] {
            JsonSerializer.Serialize(new { story = "你搬完了木柴。", turn_id = "missing-effort", base_revision = tired.Revision,
                inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>() }),
            Reply(tired, "bad-effort", "unknown"),
            Reply(tired, "bad-remove", "none", [new {op="remove_status",character="player",status_id=tired.Statuses.Single(s=>s.Category=="疲劳").Id,reason="口头说不累"}]),
            Reply(tired, "bad-create", "none", [new {op="create_status",character="player",name="疲劳",category="疲劳",severity="轻微",description="自行改状态",effects="",reason="错误字段"}]) })
        {
            using var doc = JsonDocument.Parse(invalid); string turn = doc.RootElement.GetProperty("turn_id").GetString()!;
            string unchanged = tired.Snapshot();
            try { InventoryReply.Apply(new(unchanged, turn, Scenario: scenario), invalid); throw new Exception("应拒绝越权疲劳操作"); }
            catch (InvalidOperationException) { Check(tired.Snapshot() == unchanged, "非法强度或直接修改主角疲劳时整轮原子拒绝"); }
        }
        var atForest = InventoryState.Read(tired.Snapshot());
        atForest.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "你在林中。" });
        atForest.FatiguePoints = 50; FatigueSystem.SyncStatus(atForest);
        var outcome = FatigueDice.Resolve(atForest, scenario, "hunt", 17);
        using var prompt = JsonDocument.Parse(PromptBuilder.User(new AiRequest("背景", "开始狩猎", "rain_room", "第1天", 4096, 0, "",
            Inventory: new(atForest.Snapshot(), "roll", outcome, Scenario: scenario)), []));
        var ctx = prompt.RootElement.GetProperty("program_context");
        Check(ctx.GetProperty("fatigue").GetProperty("points").GetDecimal() == 50 &&
            ctx.GetProperty("program_action").GetProperty("raw_roll").GetInt32() == 17 &&
            ctx.GetProperty("program_action").GetProperty("roll").GetInt32() == 13 &&
            ctx.GetProperty("program_action").GetProperty("fatigue_penalty").GetInt32() == 4,
            "AI资料明确区分疲劳、原骰点和有效骰点，避免二次扣减");
        var active = InventoryState.Read(tired.Snapshot());
        var accepted = InventoryReply.Apply(new(active.Snapshot(), "once", Scenario: scenario), Reply(active, "once", "light"));
        var adopted = InventoryState.Read(accepted.Snapshot);
        try { InventoryReply.Apply(new(adopted.Snapshot(), "once", Scenario: scenario), Reply(adopted, "once", "light")); throw new Exception("应拒绝重放"); }
        catch (InvalidOperationException) { Check(adopted.FatiguePoints == 35, "已采用回合不能重放来重复增加疲劳"); }
    }
}
