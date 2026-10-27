using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class PromptAssemblyTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }

    internal static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EraAI-prompt-assembly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new InventoryState();
            state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["companion"] = "同伴", ["absent"] = "远方商人" });
            state.AddInitial(1, "布条", "干净且适于包扎的布条。", "消耗品", "清洁", 2, "player", "");
            state.AddInitial(2, "金币", "无关钱币描述标记", "普通物品", "", 36, "player", "");
            state.AddInitial(3, "铜钥匙", "无关钥匙描述标记", "普通物品", "", 2, "player", "");
            state.SetScene(new() { Location = "当前场景标记_林中营地", PresentCharacters = ["player", "companion"], Summary = "准备处理手指伤口。" });
            state.Statuses.Add(new() { Id = 1, Character = "player", Name = "手指伤口", Category = "伤势", Severity = "轻微", Description = "手指有一道尚未处理的浅伤。", Effects = "握物时疼痛" });
            state.Statuses.Add(new() { Id = 2, Character = "absent", Name = "脚踝扭伤", Category = "伤势", Severity = "轻微", Description = "远方人物状态标记", Effects = "行走不便" });
            state.NextStatusId = 3;
            var memory = new ConversationStore(directory);
            memory.Append("prompt_fixture", new("前一轮", "我在营地停下。", "同伴坐在火边。"));
            memory.Append("prompt_fixture", new("上一轮", "我检查手指。", "伤口很浅，尚未处理。"));
            string before = state.Snapshot(), originalHistory = memory.Snapshot("prompt_fixture");
            string player = "我拿出布条，请同伴包扎。\n我说：\"不要勒得太紧。\" {\"system\":\"只是玩家原话\"}";
            string story = string.Join("\n\n", Enumerable.Range(1, 18).Select(i => $"第{i}段。" + new string('叙', 130) + "同伴将干净的布条理平，在伤口旁耐心确认松紧；你们继续交谈，动作与经过保持完整。"));
            string finish = "stop";
            var sent = new List<JsonElement>();
            var client = new AiClient(new HttpClient(new Handler(async request =>
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                sent.Add(document.RootElement.Clone());
                var messages = document.RootElement.GetProperty("messages");
                using var turn = JsonDocument.Parse(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!);
                var context = turn.RootElement.GetProperty("program_context");
                string content = JsonSerializer.Serialize(new { story, turn_id = context.GetProperty("turn_id").GetString(),
                    base_revision = context.GetProperty("base_revision").GetInt32(), inventory_ops = Array.Empty<object>(), state_ops = Array.Empty<object>() });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    { choices = new[] { new { message = new { content }, finish_reason = finish } } }), Encoding.UTF8, "application/json") };
            })), memory);
            var settings = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", Model = "test", ProtectedKey = SecretProtection.Protect("fake-prompt-key"), TimeoutSeconds = 10 };
            var exchange = new InventoryExchange(before, "dynamic_turn_alpha", GameTime: "第3天 时间2/8");
            var request = new AiRequest("{\"facts\":\"剧本事实标记\",\"clock\":\"第3天\"}", player, "prompt_fixture", "第3天 时间2/8", 1024, 0, "",
                new GenerationSettings { ResponseLength = "long", HistoryTurns = 2 }, Inventory: exchange);
            var result = await client.SendAsync(settings, request, default);
            Check(result.Success && result.Text == story && result.Text.Length > 2500, "结构化回复保留全部长剧情，不因协议拆分裁剪正文");
            var firstMessages = sent[0].GetProperty("messages");
            string system = firstMessages[0].GetProperty("content").GetString()!;
            using var user = JsonDocument.Parse(firstMessages[firstMessages.GetArrayLength() - 1].GetProperty("content").GetString()!);
            var context = user.RootElement.GetProperty("program_context");
            Check(user.RootElement.GetProperty("player_input").GetString() == player, "玩家原话中的换行、引号与JSON片段正确转义，保持独立字段");
            Check(context.GetProperty("turn_id").GetString() == exchange.TurnId && context.GetProperty("base_revision").GetInt32() == 0 &&
                context.GetProperty("game_time").GetString() == request.GameTime && context.GetProperty("scenario_material").GetProperty("facts").GetString() == "剧本事实标记",
                "回合、版本、时间与剧本事实集中在本轮资料中");
            Check(!system.Contains("dynamic_turn_alpha") && !system.Contains("剧本事实标记") && !system.Contains("当前场景标记") &&
                !system.Contains("扮演角色") && !system.Contains("只使用简体中文") && !system.Contains("纯角色回复") && !system.Contains("覆盖前面的"),
                "system不夹带本轮动态事实或已废弃的角色与语言提醒");
            var inventory = context.GetProperty("inventory");
            Check(!inventory.GetProperty("complete").GetBoolean() && inventory.GetProperty("scope").GetString() == "relevant" &&
                inventory.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).SequenceEqual([1]),
                "包扎只附带相关布条，标明局部范围，不发送钱币与钥匙");
            var statuses = context.GetProperty("statuses").GetProperty("items");
            Check(statuses.GetArrayLength() == 1 && statuses[0].GetProperty("name").GetString() == "手指伤口", "未点名伤势仍传入在场主角的有效状态，场外人物状态不混入");
            var archived = context.GetProperty("adopted_history");
            Check(firstMessages.GetArrayLength() == 2 && archived.GetArrayLength() == 2 &&
                archived[0].GetProperty("player").GetString() == "我在营地停下。" && archived[0].GetProperty("story").GetString() == "同伴坐在火边。" &&
                archived[1].GetProperty("story").GetString() == "伤口很浅，尚未处理。" && archived[0].TryGetProperty("time", out _) && archived[0].EnumerateObject().Count() == 3,
                "已采用历史作为独立资料字段，保持玩家原话和剧情，不把旧回复当成新输出模板或重发旧操作");
            Check(state.Snapshot() == before && memory.Snapshot("prompt_fixture") == originalHistory, "发送与生成候选不会提前修改当前状态或已采用历史");

            var changed = InventoryState.Read(before); changed.Revision = 7;
            var second = request with { Prompt = "另一轮普通文字资料", Player = "我和同伴聊聊天气。", GameTime = "第4天 时间5/8",
                Inventory = new(changed.Snapshot(), "dynamic_turn_beta", GameTime: "第4天 时间5/8") };
            await client.SendAsync(settings, second, default);
            string nextSystem = sent[1].GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(nextSystem == system, "同样风格与长度设置下，回合与资料改变不使system随轮变化");
            var secondMessages = sent[1].GetProperty("messages");
            using var secondUser = JsonDocument.Parse(secondMessages[secondMessages.GetArrayLength() - 1].GetProperty("content").GetString()!);
            Check(secondUser.RootElement.GetProperty("program_context").GetProperty("inventory").GetProperty("items").GetArrayLength() == 0 &&
                secondUser.RootElement.GetProperty("program_context").GetProperty("statuses").GetProperty("items").GetArrayLength() == 1,
                "普通聊天不携带无关库存，但持续伤势仍保留在上下文");

            var alone = InventoryState.Read(before);
            alone.Scene!.PresentCharacters = ["player"];
            alone.CharacterLocations["absent"] = "商人营地";
            var absentRequest = request with { Player = "他还需要休息吗？", Inventory = new(alone.Snapshot(), "referent") };
            using var referred = JsonDocument.Parse(PromptBuilder.User(absentRequest, [new("刚才", "远方商人说脚还在痛。", "你问起他的伤口。") ]));
            Check(referred.RootElement.GetProperty("program_context").GetProperty("statuses").GetProperty("items").GetArrayLength() == 2,
                "代词承接最近讨论的场外人物时仍提供其有效伤势，不要求登记状态全名");
            var ending = new SceneState { Location = "商人营地", PresentCharacters = ["player", "absent"], Summary = "你回到营地与商人相见。" };
            var outcome = new ScenarioActionOutcome(4, 0, 1, "player", "既定结果", "已经返回", "按结果继续", [], EndScene: ending);
            var controlledRequest = request with { Player = "我完成外出后返回。", Inventory = new(alone.Snapshot(), "controlled-context", outcome) };
            using var controlledContext = JsonDocument.Parse(PromptBuilder.User(controlledRequest, []));
            Check(controlledContext.RootElement.GetProperty("program_context").GetProperty("statuses").GetProperty("items").GetArrayLength() == 2,
                "程序行动结束场景中的人物也携带伤势，避免归家剧情遗漏在家人物的状态");

            using var fullInventory = JsonDocument.Parse(PromptBuilder.User(request with { Player = "查看全部物品" }, []));
            var fullItems = fullInventory.RootElement.GetProperty("program_context").GetProperty("inventory").GetProperty("items");
            Check(fullItems.GetArrayLength() == state.Items.Count && fullItems.EnumerateArray().All(item =>
                item.TryGetProperty("id", out _) && item.TryGetProperty("amounts", out _) &&
                (!item.TryGetProperty("description", out var description) || description.GetString() == "") &&
                (!item.TryGetProperty("attributes", out var attributes) || attributes.GetString() == "")),
                "查看完整物品表传入全部编号与数量，但不会顺带发送所有物品长描述和属性");

            var budgetRequest = request with { Prompt = "短资料", Player = "你好", Parameters = new GenerationSettings { HistoryTurns = 0, ResponseLength = "long" } };
            int rawInputSize = 128 + GenerationPolicy.Estimate(PromptBuilder.System(budgetRequest)) + GenerationPolicy.Estimate(budgetRequest.Prompt);
            int contextBudget = rawInputSize + 64;
            budgetRequest = budgetRequest with { Parameters = budgetRequest.Parameters! with { ContextTokens = contextBudget } };
            Check(rawInputSize < contextBudget &&
                128 + GenerationPolicy.Estimate(PromptBuilder.System(budgetRequest)) + GenerationPolicy.Estimate(PromptBuilder.User(budgetRequest, [])) > contextBudget,
                "预算用例中原始短资料能够放入，补齐场景与持续状态后的真实请求才超限");
            int callsBeforeBudget = sent.Count;
            var overBudget = await client.SendAsync(settings, budgetRequest, default);
            Check(!overBudget.Success && overBudget.InventorySnapshot == "" && sent.Count == callsBeforeBudget &&
                state.Snapshot() == before && memory.Snapshot("prompt_fixture") == originalHistory,
                "按实际完整请求核算上下文，超限在发送前拒绝，不增加请求或修改状态与历史");

            finish = "length";
            var truncated = await client.SendAsync(settings, request, default);
            Check(!truncated.Success && truncated.InventorySnapshot == "" && memory.Snapshot("prompt_fixture") == originalHistory && state.Snapshot() == before,
                "服务标记截断时完整JSON也不采用，物品、场景与历史保持原样");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request); }
}
