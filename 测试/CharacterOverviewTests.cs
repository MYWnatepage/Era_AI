using MinorShift.Emuera.NativeAI;

internal static class CharacterOverviewTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }

    internal static void Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["ella"] = "艾拉", ["player"] = "主角" });
        state.SetScene(new() { Location = "森林边的小屋", PresentCharacters = ["player", "ella"], Summary = "两人留在家中。" });
        string original = state.Snapshot();
        var overview = CharacterOverview.Page(state, 0);
        Check(overview[11] == "2" && overview[12] == "player" && overview[13] == "ella" && overview[0].Contains("森林边的小屋"),
            "没有伤病时人物概览仍列出主角、艾拉及当前所在地");
        Check(overview[0].Contains("暂无状态记录") && !overview[0].Contains("健康") && CharacterOverview.Detail(state, "ella", 0)[2] == "0",
            "空状态表明确暂无记录，不虚构健康值或伤势");
        Check(state.Snapshot() == original, "只读查看人物概览和详情不修改本局快照");

        state.Statuses.Add(new() { Id = 1, Character = "ella", Name = "手指划伤", Category = "伤势", Severity = "轻微",
            Description = "伤口已包扎，尚未痊愈。", Effects = "触碰时疼痛", CreatedAt = "第1天", UpdatedAt = "第1天" });
        state.NextStatusId = 2;
        state.SetScene(new() { Location = "溪边", PresentCharacters = ["player"], Summary = "主角独自来到溪边。" });
        string saved = state.Snapshot();
        var ella = CharacterOverview.Detail(state, "ella", 0);
        var player = CharacterOverview.Detail(state, "player", 0);
        Check(ella[0] == "艾拉" && ella[1] == "森林边的小屋" && ella[2] == "1" && ella[4].Contains("手指划伤") && ella[14] == "1",
            "人物详情按所属人物筛选已保存伤势，并保留场外人物所在地");
        Check(player[1] == "溪边" && player[2] == "0" && player[4] == "" && player[14] == "",
            "主角详情不会混入艾拉的状态或沿用她的地点");
        Check(CharacterOverview.Detail(InventoryState.Read(saved), "ella", 0).SequenceEqual(ella) && state.Snapshot() == saved,
            "人物展示读取存档中的现状，保存读取前后相同且不回写初始人物卡");
        var unknown = new InventoryState();
        unknown.BindCharacters(state.Characters);
        Check(CharacterOverview.Detail(unknown, "ella", 0)[1] == "未知", "旧档缺少人物所在地时明确显示未知，不假定人物在开场地点");
        try { CharacterOverview.Detail(state, "stranger", 0); throw new Exception("未拒绝未知人物"); }
        catch (ArgumentException) { Check(true, "不能通过人物详情查看未登记的人物"); }

        for (int id = 2; id <= 12; id++)
            state.Statuses.Add(new() { Id = id, Character = "ella", Name = "记录" + id, Category = "其他", Severity = "已记录",
                Description = "分页测试记录", Effects = "", CreatedAt = "第1天", UpdatedAt = "第1天" });
        state.NextStatusId = 13;
        ella = CharacterOverview.Detail(state, "ella", 999);
        Check(ella[2] == "12" && ella[3] == "2" && ella[14] == "11" && ella[15] == "12" && ella[16] == "",
            "人物状态超过一页时正确分页，并用实际状态编号打开详情");
        var many = new InventoryState();
        many.BindCharacters(Enumerable.Range(1, 12).ToDictionary(id => "c" + id.ToString("D2"), id => "人物" + id));
        var secondPage = CharacterOverview.Page(many, 1);
        Check(secondPage[10] == "2" && secondPage[11] == "12" && secondPage[12] == "c11" && secondPage[13] == "c12" && secondPage[14] == "",
            "人物列表分页保持按钮与人物编号对应，不残留前页按钮");
        Check(CharacterOverview.Page(new InventoryState(), -1)[10] == "1", "空人物登记和越界页码仍可安全返回空页");

        var fresh = new InventoryState();
        fresh.BindCharacters(state.Characters);
        fresh.EnsureCharacterProfile("player", 11, 145, 36, "健康", "无已知伤病。", true);
        fresh.EnsureCharacterProfile("ella", 7, 120, 22, "健康", "无已知伤病。", true);
        var profile = CharacterOverview.Detail(fresh, "player", 0);
        Check(profile.Skip(24).Take(5).SequenceEqual(new[] { "11 岁", "145 cm", "36 kg", "健康", "无已知伤病。" }) &&
            CharacterOverview.Page(fresh, 0)[1].Contains("7岁 / 120 cm / 22 kg / 健康"),
            "新游戏从人物卡复制身体资料及明确健康基线，列表和详情均显示");
        fresh.Statuses.Add(new() { Id = 1, Character = "player", Name = "左臂划伤", Category = "伤势", Severity = "轻微",
            Description = "已包扎未愈", Effects = "", CreatedAt = "第1天", UpdatedAt = "第1天" });
        fresh.NextStatusId = 2;
        Check(CharacterOverview.Detail(fresh, "player", 0)[27] == "左臂划伤" && CharacterOverview.Detail(fresh, "player", 0)[28] == "",
            "受伤后身体状况显示有效伤势，不继续显示初始健康说明");
        fresh.EnsureCharacterProfile("player", 99, 999, 999, "健康", "不能覆盖", false);
        var restored = InventoryState.Read(fresh.Snapshot());
        Check(restored.CharacterProfiles["player"].Age == 11 && restored.CharacterHealth("player") == "左臂划伤",
            "保存读取及重复初始化保留本局身体资料和伤病，不用卡片覆盖进度");
        restored.Statuses.Clear();
        Check(restored.CharacterHealth("player") == "健康", "明确移除全部持续状态后显示已知健康基线");
        var legacy = InventoryState.Read(saved);
        string statuses = System.Text.Json.JsonSerializer.Serialize(legacy.Statuses);
        legacy.EnsureCharacterProfile("ella", 7, 120, 22, "健康", "初始健康", false);
        legacy.EnsureCharacterProfile("player", 11, 145, 36, "健康", "初始健康", false);
        Check(System.Text.Json.JsonSerializer.Serialize(legacy.Statuses) == statuses && legacy.Revision == state.Revision &&
            CharacterOverview.Detail(legacy, "ella", 0)[27] == "手指划伤" && legacy.CharacterHealth("player") == "暂无状态记录",
            "旧档仅补缺失的身体资料，保留伤势且不把未知健康写为初始健康");
        try { fresh.EnsureCharacterProfile("stranger", 10, 140, 35, "健康", "", true); throw new Exception("未拒绝未知人物资料"); }
        catch (ArgumentException) { Check(true, "基础资料仍受当前剧本人物登记约束"); }
    }
}
