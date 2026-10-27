using System.Text.Json;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.Runtime.Utils;

internal static class ScenarioIsolationTests
{
    private static void Check(bool value, string text)
    { if (!value) throw new Exception(text); Console.WriteLine("通过：" + text); }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraAI-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Package(string folder, ScenarioDefinition definition)
        {
            string path = Path.Combine(root, folder); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "剧本.json"), JsonSerializer.Serialize(definition, InventoryState.Json));
            File.WriteAllText(Path.Combine(path, "流程.ERB"), "@ORBIT_INIT\nRETURN 1\n@ORBIT_START\nRETURN\n@ORBIT_RESUME\nRETURN\n");
        }
        var scenario = new ScenarioDefinition
        {
            Id = "orbital", Name = "轨道站", Author = "test", Type = "独立验证", Entry = "ORBIT_START", Init = "ORBIT_INIT", Resume = "ORBIT_RESUME",
            Owners = new() { ["captain"] = "舰长", ["engineer"] = "工程师", ["robot"] = "机器人", ["vault"] = "仓库" },
            ReplyLabel = "值班记录", DefaultSuggestions = ["检查反应堆"], Parameters = new() { ["shift_length"] = 12 },
            Commands = new() { ["7"] = ["维修"] }, CommandPrefixes = ["", "开始"], CommandSuffixes = [""],
            Actions = new() { ["repair"] = new() {
                Sides = 6, ItemId = 77, Owner = "captain", Summary = "D6={roll} 晶体+{quantity}", Story = "获得{quantity}颗晶体。", Instruction = "晶体增加{quantity}颗，物品操作必须为空。",
                QuantityPatterns = [@"获得(?<quantity>\d+)颗晶体"],
                Outcomes = [new() { Min = 1, Max = 3, Quantity = 0, Tier = "无", Event = "无" }, new() { Min = 4, Max = 6, Quantity = 2, Tier = "完成", Event = "修复" }]
            } }
        };
        try
        {
            Check(ScenarioCatalog.Load(root).Entries.Count == 0, "移走所有剧本后，登记服务保持可用");
            string legacyPath = Path.Combine(root, "save99.sav");
            File.WriteAllText(legacyPath, "original legacy save");
            ScenarioSavePaths.Select("orbital", scenario.MatchesSave);
            string orbitalPath = ScenarioSavePaths.Resolve(root, "save99.sav", true);
            Directory.CreateDirectory(Path.GetDirectoryName(orbitalPath)!); File.WriteAllText(orbitalPath, "station save");
            ScenarioSavePaths.Select("another");
            string anotherPath = ScenarioSavePaths.Resolve(root, "save99.sav", true);
            Directory.CreateDirectory(Path.GetDirectoryName(anotherPath)!); File.WriteAllText(anotherPath, "another save");
            ScenarioSavePaths.Select("orbital", scenario.MatchesSave);
            Check(File.ReadAllText(ScenarioSavePaths.Resolve(root, "save99.sav")) == "station save" && File.ReadAllText(anotherPath) == "another save" && File.ReadAllText(legacyPath) == "original legacy save",
                "同号手动/自动存档按剧本分目录，保留旧根目录存档不覆盖");
            Package("station", scenario);
            var catalog = ScenarioCatalog.Load(root);
            Check(catalog.Errors.Count == 0 && catalog.Entries.Count == 1 && catalog.Get("orbital").Parameter("shift_length") == 12,
                "独立剧本由文件夹登记，规则不需要修改框架");
            Check(scenario.MatchesSave("[scenario:orbital] save 1") && !scenario.MatchesSave("[scenario:orbital2] 轨道站"), "存档按完整剧本编号分组");
            Check(scenario.MatchesSave("2026/10/22 12:34:56 [scenario:orbital] save 1") &&
                ScenarioSavePaths.DisplayCaption("2026/10/22 12:34:56 [scenario:orbital] save 1") == "2026/10/22 12:34:56 save 1",
                "内置手动存档的时间前缀可识别，界面不显示内部剧本标记");
            Check(!scenario.MatchesSave("其他剧本 提到轨道站"), "标题包含剧本名不再误判为该剧本存档");
            var legacy = ScenarioFixture.Ella;
            Check(legacy.MatchesSave("2026/10/22 12:34:56 雨夜房间 第3天") && legacy.MatchesSave("雨夜房间 第3天") && legacy.MatchesSave("自动存档：艾拉的奇幻冒险"), "旧存档别名来自剧本登记，保留读取兼容");
            Check(!legacy.MatchesSave("[scenario:orbital] 艾拉的奇幻冒险"), "机器编号优先，不能被标题中的旧别名串档");
            var state = new InventoryState(); state.BindOwners(scenario.Owners);
            state.AddInitial(2, "导航图", "导航图", "普通物品", "", 1, "vault", "");
            state.AddInitial(77, "晶体", "能源晶体", "消耗品", "", 5, "captain", "");
            foreach (string owner in scenario.Owners.Keys) state.Item(77).Quantities[owner] = 1;
            Check(InventoryState.Read(state.Snapshot()).Item(77).Quantities.Count == 4, "任意登记角色可持有物品，不限于三个固定归属");
            state.SetQuantity(2, "vault", 9); state.Consume(77, 2, ["captain", "engineer"]);
            Check(state.Quantity(2) == 9 && state.Quantity(77) == 2, "2号物品不再隐含食材，消耗对象与归属由剧本指定");
            var action = scenario.Resolve("repair", 6);
            var context = new InventoryExchange(state.Snapshot(), "orbital-turn", action, scenario.Id);
            string response = JsonSerializer.Serialize(new { story = "获得99颗晶体。", turn_id = context.TurnId, base_revision = state.Revision, inventory_ops = Array.Empty<object>() });
            var applied = InventoryReply.Apply(context, response);
            Check(InventoryState.Read(applied.Snapshot).Quantity(77) == 4 && applied.Story == "获得2颗晶体。" && !applied.RequiresApproval,
                "不同骰子、收益物品和正文校验可由另一剧本独立配置");
            string prompt = InventoryReply.Instruction(new(state.Snapshot(), "orbital-plain", ScenarioId: scenario.Id));
            var request = new AiRequest("轨道站维修资料", "检查晶体", scenario.Id, "shift 1", 1024, 0, "", Inventory: new(state.Snapshot(), "orbital-plain"));
            string material = PromptBuilder.User(request, []);
            Check(!prompt.Contains("captain") && material.Contains("captain") && !prompt.Contains("艾拉") && !prompt.Contains("ella") && !prompt.Contains("食材") && !prompt.Contains("狩猎"),
                "通用物品协议没有艾拉剧本的设定泄漏");
            string bad = JsonSerializer.Serialize(new { story = "不应发生", turn_id = "orbital-plain", base_revision = state.Revision,
                inventory_ops = new[] { new { op = "change_quantity", item_id = 77, owner = "ella", delta = 1, reason = "未登记角色" } } });
            bool rejected = false;
            try { InventoryReply.Apply(new(state.Snapshot(), "orbital-plain"), bad); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "另一剧本不能把物品转给未登记的艾拉");
            Check(ActionRules.CommonAction("开始维修", scenario) == 7 && ActionRules.CommonAction("外出狩猎", scenario) == 0 && ActionRules.CommonAction("不维修", scenario) == 0,
                "行动识别只使用当前剧本的明确指令");
            Check(ActionRules.SuggestionList("", scenario.DefaultSuggestions).Single() == "检查反应堆" && ActionRules.SuggestionList("").Length == 0,
                "默认建议来自剧本，无剧本时不会出现艾拉相关建议");
            using var raw = JsonDocument.Parse(state.Snapshot());
            var old = raw.RootElement.EnumerateObject().Where(p => p.Name != "owners").ToDictionary(p => p.Name, p => p.Value.Clone());
            var migrated = InventoryState.Read(JsonSerializer.Serialize(old)); migrated.BindOwners(scenario.Owners);
            Check(migrated.Quantity(77) == state.Quantity(77) && migrated.Revision == state.Revision && migrated.Holdings(migrated.Item(2)).Contains("仓库"),
                "旧物品快照补齐剧本归属显示名，不重置数量或版本");
            Package("duplicate", scenario);
            Check(ScenarioCatalog.Load(root).Entries.Count == 0 && ScenarioCatalog.Load(root).Errors.Any(e => e.Contains("重复")), "重复剧本编号拒绝载入，避免串档");
            Directory.Delete(Path.Combine(root, "duplicate"), true);
            File.WriteAllText(Path.Combine(root, "station", "流程.ERB"), "@ORBIT_INIT\nRETURN\n");
            Check(ScenarioCatalog.Load(root).Entries.Count == 0 && ScenarioCatalog.Load(root).Errors.Any(e => e.Contains("入口")), "缺少剧本入口时在选择页报告，不执行不存在的函数");
            Package("station", scenario);
            File.WriteAllText(Path.Combine(root, "station", "剧本.json"), "{broken");
            Check(ScenarioCatalog.Load(root).Entries.Count == 0 && ScenarioCatalog.Load(root).Errors.Count == 1, "损坏登记不拖垮框架启动");
        }
        finally { Directory.Delete(root, true); }
    }
}
