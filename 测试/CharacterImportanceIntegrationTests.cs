using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;

internal static class CharacterImportanceIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Flags)!.Invoke(obj, args);
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS character importance: " + message); }

    internal static void Run(string assemblyPath, string gamePath)
    {
        PrepareFixture(gamePath);
        Exception? failure = null;
        var thread = new Thread(() => { try { Observe(assemblyPath, gamePath); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new Exception("Character importance UI timed out.");
        if (failure != null) throw failure;
    }

    private static void PrepareFixture(string gamePath)
    {
        if (!File.Exists(Path.Combine(gamePath, "importance-ui-isolated.txt")))
            throw new ArgumentException("This test may patch only its marked isolated game copy.");
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState();
        state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
        state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.AddInitial(1, "铜钥匙", "用于小屋门锁的铜钥匙。", "普通物品", "", 2, "player", "");
        state.AddInitial(2, "食材", "可供烹饪的食材。", "消耗品", "", 10, "player", "");
        state.EnsureCharacterProfile("player", 11, 145, 36, "健康", "无已知伤病。", true);
        state.EnsureCharacterProfile("ella", 7, 120, 22, "健康", "无已知伤病。", true);
        state.Characters["npc_1"] = state.Owners["npc_1"] = "罗莎";
        string description = "罗莎是村落集市的盐商，围着蓝围巾，说话干脆。" +
            string.Concat(Enumerable.Repeat("她记得熟客的习惯，也仔细清点每批货物。", 12)) + "归档末尾特征保留。";
        state.DynamicCharacters["npc_1"] = new() { Name = "罗莎", Description = description, CreatedAt = "第2天" };
        state.NextCharacterId = 2; state.CharacterLocations["npc_1"] = state.Scene!.Location;
        state.Scene.PresentCharacters.Add("npc_1");
        state.Statuses.Add(new() { Id = 11, Character = "npc_1", Name = "左脚扭伤", Category = "伤势", Severity = "中度",
            Description = "左脚扭伤尚未愈合，已有包扎，行走仍然疼痛。", Effects = "行走较慢，暂不能负重。",
            CreatedAt = "第2天 时间1/8", UpdatedAt = "第2天 时间2/8" });
        state.NextStatusId = 12; state.Revision = 4;
        File.WriteAllText(Path.Combine(gamePath, "importance-seed.txt"), state.Snapshot(), new UTF8Encoding(false));

        string flow = Path.Combine(gamePath, "ERB/script/艾拉的奇幻冒险/流程.ERB");
        string start = "@SCENARIO_TEST_RAIN_ROOM_START\n";
        string text = File.ReadAllText(flow).ReplaceLineEndings("\n");
        if (!text.Contains(start, StringComparison.Ordinal)) throw new Exception("Missing scenario start hook.");
        text = text.Replace(start, start + """
IF GLOBAL:892 == 0
    SAVESTR:61 '= LOADTEXT("importance-seed.txt", 0, 1)
    DAY = 3
    TIME = 2
    FLAG:20 = 10
    FLAG:21 = 1
    FLAG:22 = 2
    GLOBAL:892 = 1
ENDIF

""", StringComparison.Ordinal);
        string input = "$SCENARIO_TEST_RAIN_ROOM_INPUT\nESCAPE 0\nINPUT\n";
        if (!text.Contains(input, StringComparison.Ordinal)) throw new Exception("Missing scenario menu hook.");
        text = text.Replace(input, """
$SCENARIO_TEST_RAIN_ROOM_INPUT
CALL IMPORTANCE_CAPTURE
ESCAPE 0
INPUT
IF RESULT == 8888
    CALL RAIN_ROOM_REVIEW_ITEMS, "本地提案只读检查", "仅检查人物查看，不采用任何状态变化。", "测试夹具", ""
    GOTO SCENARIO_TEST_RAIN_ROOM_MAIN
ENDIF

""", StringComparison.Ordinal);
        File.WriteAllText(flow, text, new UTF8Encoding(true));
        string status = Path.Combine(gamePath, "ERB/script/艾拉的奇幻冒险/人物状态.ERB");
        text = File.ReadAllText(status).ReplaceLineEndings("\n").Replace("ESCAPE 0\nINPUT", "CALL IMPORTANCE_CAPTURE\nESCAPE 0\nINPUT", StringComparison.Ordinal);
        File.WriteAllText(status, text, new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(gamePath, "ERB/IMPORTANCE_UI_TEST.ERB"), """
@IMPORTANCE_CAPTURE
SAVETEXT SAVESTR:61, "importance-state.txt", 0, 1
SAVETEXT STRFORM("{DAY},{TIME},{FLAG:20}"), "importance-clock.txt", 0, 1
RETURN

""", new UTF8Encoding(true));
    }

    private static void Observe(string assemblyPath, string gamePath)
    {
        string folder = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Exception? failure = null; bool finished = false; int step = 0;
        string baseline = "", clock = "", promoted = "", final = "";
        var watch = Stopwatch.StartNew(); using var timer = new System.Windows.Forms.Timer { Interval = 90 };
        timer.Tick += (_, _) =>
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "MinorShift.Emuera.Forms.MainWindow");
            if (form == null) return;
            try
            {
                if (watch.Elapsed.TotalSeconds > 45) throw new Exception("Character importance stalled at step " + step);
                var console = Get(form, "console"); if (console == null || (bool)Get(console, "IsInProcess")!) return;
                string text = string.Join('\n', ((IEnumerable)Get(console, "displayLineList")!).Cast<object>());
                void Enter(string input) => Call(console, "PressEnterKey", false, input, false);
                bool Main() => text.Contains("[10] 查看人物状态");
                bool List() => text.Contains("当前人物状态") && text.Contains("共 3 位人物");
                bool Detail() => text.Contains("人物分类：") && text.Contains("[0] 返回人物列表") && text.Contains("罗莎");
                bool Review() => text.Contains("本轮剧情与状态变化") && text.Contains("[3] 查看人物状态");
                string Snapshot() => File.ReadAllText(Path.Combine(gamePath, "importance-state.txt")).TrimStart('\uFEFF');
                string Clock() => File.ReadAllText(Path.Combine(gamePath, "importance-clock.txt")).TrimStart('\uFEFF');
                void SameFacts(string snapshot, string expectedRole, int revisions)
                {
                    var before = InventoryState.Read(baseline); var current = InventoryState.Read(snapshot);
                    Check(current.ImportanceOf("npc_1") == expectedRole && current.Revision == before.Revision + revisions,
                        "confirmed importance and revision match the actual menu action");
                    Check(JsonSerializer.Serialize(before.Items, InventoryState.Json) == JsonSerializer.Serialize(current.Items, InventoryState.Json) &&
                        JsonSerializer.Serialize(before.Statuses, InventoryState.Json) == JsonSerializer.Serialize(current.Statuses, InventoryState.Json) &&
                        JsonSerializer.Serialize(before.CharacterLocations, InventoryState.Json) == JsonSerializer.Serialize(current.CharacterLocations, InventoryState.Json) &&
                        before.DynamicCharacters["npc_1"].Description == current.DynamicCharacters["npc_1"].Description && Clock() == clock,
                        "classification preserves inventory, wounds, location, full portrait, food and clock");
                }
                switch (step)
                {
                    case 0 when text.Contains("[0] 开始新游戏"): Enter("0"); break;
                    case 1 when text.Contains("剧本选择"): Enter("1"); break;
                    case 2 when Main(): Enter("10"); break;
                    case 3 when List():
                        Check(text.Contains("主角 / 主要人物") && text.Contains("艾拉 / 主要人物") && text.Contains("罗莎 / 次要人物"),
                            "real list shows the two major characters and a minor NPC");
                        baseline = Snapshot(); clock = Clock(); Enter("3"); break;
                    case 4 when Detail():
                        Check(text.Contains("次要人物") && text.Contains("[1003] 设为主要人物") && !text.Contains("年龄：") && !text.Contains("身高：") && !text.Contains("体重："),
                            "minor detail omits unknown body fields and provides an upgrade action");
                        string unwrapped = text.Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
                        if (!unwrapped.Contains("归档末尾特征保留") || !unwrapped.Contains("左脚扭伤"))
                            Console.WriteLine("DETAIL DIAGNOSTIC: " + text);
                        Check(unwrapped.Contains("归档末尾特征保留") && unwrapped.Contains("左脚扭伤"), "detail retains full archived portrait and actual wound");
                        Check(Snapshot() == baseline, "browsing does not change current state"); Enter("1003"); break;
                    case 5 when text.Contains("将罗莎设为主要人物？"):
                        Check(Snapshot() == baseline, "opening importance confirmation does not mutate a save"); Enter("1"); break;
                    case 6 when text.Contains("人物分类已更新"):
                        Enter(""); break;
                    case 7 when Detail() && text.Contains("[1003] 设为次要人物"):
                        promoted = Snapshot(); SameFacts(promoted, "major", 1); Enter("1003"); break;
                    case 8 when text.Contains("将罗莎设为次要人物？"): Enter("0"); break;
                    case 9 when Detail() && text.Contains("[1003] 设为次要人物"):
                        Check(Snapshot() == promoted, "cancelled downgrade keeps the confirmed major character intact"); Enter("1003"); break;
                    case 10 when text.Contains("将罗莎设为次要人物？"): Enter("1"); break;
                    case 11 when text.Contains("人物分类已更新"): Enter(""); break;
                    case 12 when Detail() && text.Contains("[1003] 设为主要人物"):
                        final = Snapshot(); SameFacts(final, "minor", 2); Enter("0"); break;
                    case 13 when List(): Enter("0"); break;
                    case 14 when Main(): Enter("8888"); break;
                    case 15 when Review(): Enter("3"); break;
                    case 16 when List(): Enter("3"); break;
                    case 17 when Detail():
                        Check(!text.Contains("[1003]") && Snapshot() == final, "proposal review opens a read-only character detail");
                        Enter("1003"); break;
                    case 18 when Detail():
                        Check(!text.Contains("将罗莎设为") && Snapshot() == final, "typing the hidden importance command in review has no effect");
                        Enter("0"); break;
                    case 19 when List(): Enter("0"); break;
                    case 20 when Review(): Enter("0"); break;
                    case 21 when Main():
                        Check(Snapshot() == final && Clock() == clock, "cancelled review preserves final state and elapsed time");
                        finished = true; timer.Stop(); form.Close(); return;
                    default: return;
                }
                step++; form.Hide();
            }
            catch (Exception error) { failure = error; timer.Stop(); form.Close(); }
        };
        EventHandler? begin = null;
        begin = (_, _) => { Application.Idle -= begin; timer.Start(); };
        Application.Idle += begin;
        assembly.EntryPoint!.Invoke(null, [new string[] { "--ExeDir", gamePath }]);
        if (failure != null) throw failure;
        Check(finished, "actual native game completed importance changes and read-only proposal review");
    }
}
