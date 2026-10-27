using MinorShift.Emuera.NativeAI;

internal static class InventoryGridTests
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine("通过：" + description);
    }
    private static void Reject(Action action, string description)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException)
        { Check(true, description); return; }
        throw new Exception("未拒绝：" + description);
    }

    internal static void Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["ella"] = "艾拉" });
        state.Scene = new() { Location = "小屋", PresentCharacters = ["player"], Summary = "" };
        for (int id = 1; id <= 25; id++)
            state.AddInitial(id, "物品" + id, "完整描述" + id, "普通物品", "", 1, "player", "");
        state.Item(2).Name = "这是一个需要截断显示而详情仍应完整保留的长物品名称";
        state.Item(3).Name = "布条\n干净";
        string before = state.Snapshot();
        var first = InventoryGrid.Build(state, 0, [2, 20], 4);
        var last = InventoryGrid.Build(state, 99, [2, 20], 4);
        Check(first.Pages == 2 && first.Total == 25 && first.Columns == 4 && first.Cells.Count == 16 &&
            last.Page == 1 && last.Cells.Select(cell => cell.Id).SequenceEqual(Enumerable.Range(17, 9)),
            "物品矩阵每页四行，排序与末页边界正确");
        Check(first.Cells[1].Selected && !first.Cells[0].Selected && last.Cells.Single(cell => cell.Id == 20).Selected &&
            first.SelectedSummary.Contains("物品20"), "矩阵跨页保留所选物品，并汇总选择");
        Check(first.Cells.All(cell => !cell.Label.Contains('[') && !cell.Label.Contains('：')) &&
            first.Cells[1].Label.EndsWith('…') && InventoryGrid.DisplayWidth(first.Cells[1].Label) <= first.CellWidth - 2 &&
            first.Cells[2].Label == "布条 干净" && state.Snapshot() == before,
            "矩阵仅显示单行名称，长名省略且完整物品资料与数量不变");
        var narrow = InventoryGrid.Build(state, 0, [], 2, 12);
        Check(narrow.Cells.Count == 8 && narrow.Pages == 4 &&
            narrow.Cells.All(cell => InventoryGrid.DisplayWidth(cell.Label) <= 10), "窄窗口使用两列，名称不越过格子");
        state.Item(1).Quantities["player"] = 0;
        state.Item(2).Quantities = new() { ["ella"] = 1 };
        state.Item(3).Quantities = new() { ["world"] = 1 };
        var selectable = InventoryGrid.Build(state, 0, [], 4, selectableOnly: true);
        Check(!selectable.Cells.Any(cell => cell.Id is 1 or 2 or 3) && selectable.Total == 22,
            "行动前只提供主角或在场人物仍有余量的物品");
        state.Scene.PresentCharacters.Add("ella");
        Check(InventoryGrid.Build(state, 0, [], 4, selectableOnly: true).Cells.Any(cell => cell.Id == 2),
            "人物到场后其持有物品可加入本轮关联");
        Check(InventoryGrid.SelectedIds("2, 2,20").SequenceEqual(new[] { 2, 20 }), "关联编号去重且保留选择顺序");
        Reject(() => InventoryGrid.SelectedIds("2,-1"), "关联物品拒绝非法编号");
        Reject(() => InventoryGrid.SelectedIds(string.Join(',', Enumerable.Range(1, 17))), "每轮最多关联十六种物品");
        string emojiLabel = InventoryGrid.Label("绷带🩹绷带🩹绷带🩹", 8);
        Check(InventoryGrid.DisplayWidth("铜币ABC") == 7 && InventoryGrid.DisplayWidth(emojiLabel) <= 8 &&
            !emojiLabel.EnumerateRunes().Any(rune => rune.Value == 0xFFFD),
            "中英文和组合字符按显示宽度截断");

        var legacy = new InventoryState { Revision = 7, LastTurnId = "adopted-turn" };
        legacy.AddInitial(5, "铜币", "这个世界使用的铜币，兑换规则由剧本卡定义。", "普通物品", "单位=枚；类别=货币；面额=铜币", 37, "player", "铜币。");
        legacy.AddInitial(6, "左轮手枪", "父亲留给我的旧手枪。", "普通物品", "单位=把；类别=武器；保养=良好", 1, "player", "私人描述");
        var migrations = new InventoryTextMigrations { Items = [new()
        {
            ItemId = 5, Name = "铜币", OldDescription = "这个世界使用的铜币，兑换规则由剧本卡定义。",
            Description = "这个世界使用的货币。", OldAttributes = "单位=枚；类别=货币；面额=铜币", Attributes = "类别=货币",
        }, new()
        {
            ItemId = 6, Name = "左轮手枪", OldDescription = "一把左轮手枪，具体型号、口径和当前装填情况未设定。",
            Description = "一把使用转轮弹巢的手枪。", OldAttributes = "单位=把；类别=武器；相关弹药编号=7", Attributes = "类别=武器；相关弹药编号=7",
        }] };
        int nextId = legacy.NextId;
        Check(InventoryGrid.CleanLegacy(legacy, migrations) && legacy.Item(5).Description == "这个世界使用的货币。" &&
            legacy.Item(5).Attributes == "类别=货币" && legacy.Item(5).Quantities["player"] == 37,
            "旧默认物品说明定点清理，保留实际库存");
        Check(legacy.Item(6).Description == "父亲留给我的旧手枪。" &&
            legacy.Item(6).Attributes == "单位=把；类别=武器；保养=良好" && legacy.Item(6).Summary == "私人描述" &&
            legacy.Revision == 7 && legacy.LastTurnId == "adopted-turn" && legacy.NextId == nextId && !InventoryGrid.CleanLegacy(legacy, migrations),
            "默认说明清理不覆盖自定义物品资料，不改回合与编号，重复执行无副作用");
        Check(InventoryGrid.Build(new InventoryState(), -10, null, 4).Pages == 1 &&
            InventoryGrid.Build(new InventoryState(), -10, null, 4).Cells.Count == 0, "空背包正常显示一页且没有假物品");

        string migrationDirectory = Path.Combine(Path.GetTempPath(), "era-inventory-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(migrationDirectory);
        try
        {
            string file = Path.Combine(migrationDirectory, "items.json");
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(migrations, InventoryState.Json));
            var loaded = InventoryTextMigrations.Load(migrationDirectory, "items.json");
            Check(loaded.FormatVersion == 1 && loaded.Items.Count == 2 && loaded.Items[0].ItemId == 5 &&
                loaded.Items[0].Description == "这个世界使用的货币。", "物品说明迁移规则从独立剧本资源读取");
            Check(InventoryTextMigrations.Load(migrationDirectory, "").Items.Count == 0,
                "未配置迁移资源的其他剧本不受影响");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, "../outside.json"), "迁移资源不能越出剧本目录");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, file), "迁移资源不能使用绝对路径");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, "missing.json"), "已配置但缺失的迁移资源会明确拒绝");
            File.WriteAllText(file, "{\"format_version\":1,\"items\":[],\"items\":[]}");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, "items.json"), "迁移资源拒绝重复字段");
            File.WriteAllText(file, "{\"format_version\":2,\"items\":[]}");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, "items.json"), "迁移资源拒绝不支持的版本");
            File.WriteAllText(file, "{\"format_version\":1}");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, "items.json"), "迁移资源拒绝缺失必填字段");
            File.WriteAllText(file, "{\"format_version\":1,\"items\":[],\"extra\":true}");
            Reject(() => InventoryTextMigrations.Load(migrationDirectory, "items.json"), "迁移资源拒绝未知字段避免拼写错误静默失效");
            loaded.Items.Add(loaded.Items[0]);
            Reject(() => loaded.Validate(), "迁移资源拒绝重复物品编号");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(migrationDirectory)) File.Delete(file);
            Directory.Delete(migrationDirectory);
        }
    }
}
