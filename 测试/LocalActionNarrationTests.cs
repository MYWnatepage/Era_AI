using MinorShift.Emuera.NativeAI;

internal static class LocalActionNarrationTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("通过：" + message); }
    static InventoryState Fresh(ScenarioDefinition scenario)
    {
        var state = new InventoryState(); state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene); return state;
    }
    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella; var state = Fresh(scenario);
        var rest = new HashSet<string>(); var sleep = new HashSet<string>();
        var restSummaries = new HashSet<string>(); var sleepSummaries = new HashSet<string>(); var bonus = new HashSet<string>();
        for (int revision = 0; revision < 18; revision++)
        {
            state.Revision = revision; string original = state.Snapshot();
            var r = LocalActionNarration.Build(state, false, false); var s = LocalActionNarration.Build(state, true, false);
            var enhanced = LocalActionNarration.Build(state, false, true);
            rest.Add(r.Narrative); sleep.Add(s.Narrative); restSummaries.Add(r.Summary); sleepSummaries.Add(s.Summary);
            Check(enhanced.Narrative.StartsWith(r.Narrative, StringComparison.Ordinal) && enhanced.Summary == r.Summary,
                "地点加成只追加描述，不改正文或近况");
            bonus.Add(enhanced.Narrative[r.Narrative.Length..]);
            Check(LocalActionNarration.Build(state, false, false) == r && state.Snapshot() == original,
                "同一进度的预览和重试文字一致，不修改存档或使用额外骰点");
        }
        Check(rest.Count == 6 && sleep.Count == 6 && bonus.Count == 3 && restSummaries.Count == 3 && sleepSummaries.Count == 3,
            "六组入眠、六组休息、三组加成及各三组完成后近况均可轮换");
        state.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "在这里。" });
        var solo = LocalActionNarration.Build(state, false, false);
        Check(solo.Narrative.Contains("独自") && !solo.Narrative.Contains("艾拉") && solo.Narrative.Contains("狩猎林地"),
            "独处只描述主角，不带回不在场的人物，地点来自当前记录");
        state.SetScene(scenario.InitialScene);
        var together = LocalActionNarration.Build(state, true, true);
        Check(together.Narrative.Contains("这里还有艾拉。") && !together.Narrative.Contains("与艾拉一起") && !together.Narrative.Contains("起身"),
            "在场人物只说明在这里，不安排同行睡眠，也不要求主角起身");
        int injuryId = state.NextStatusId++;
        state.Statuses.Add(new() { Id = injuryId, Character = "player", Name = "腿部受伤", Category = "伤势", Severity = "中度", Description = "暂时不便站立。", Effects = "移动不便。" });
        for (int revision = 0; revision < 6; revision++)
        {
            state.Revision = revision; state.FatiguePoints = 80; FatigueSystem.SyncStatus(state); string original = state.Snapshot();
            var rested = InventoryState.Read(ScenarioActions.CompleteLocal(state, scenario, "rest").Snapshot);
            var slept = InventoryState.Read(ScenarioActions.CompleteLocal(state, scenario, "sleep").Snapshot);
            Check(rested.FatiguePoints == 55 && slept.FatiguePoints == 0 && rested.Status(injuryId).Description == "暂时不便站立。" &&
                slept.Status(injuryId).Description == "暂时不便站立。" && slept.CharacterLocations["ella"] == state.CharacterLocations["ella"] &&
                rested.Revision == revision + 1 && slept.Revision == revision + 1 && state.Snapshot() == original,
                "每组文案都沿用实际恢复规则，伤势与人物位置不变，源进度保持只读");
        }
    }
}
