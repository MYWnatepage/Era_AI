using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class NarrativeMemoryTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Check(true, message); return; }
        throw new Exception("未拒绝：" + message);
    }
    internal static void Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string, string> { ["player"] = "主角", ["ella"] = "艾拉" });
        state.AddInitial(1, "铜钥匙", "小屋门锁的钥匙", "普通物品", "", 2, "player", "");
        state.SetScene(new SceneState { Location = "森林边的小屋", PresentCharacters = ["player", "ella"] });
        string original = state.Snapshot();
        object Memory(string summary = "主角答应次日带艾拉去集市。", object? keywords = null) =>
            new { summary, keywords = keywords ?? new[] { "艾拉", "集市", "约定" } };
        string Reply(InventoryState current, object proposals) => JsonSerializer.Serialize(new {
            story = "你答应明天去集市。", turn_id = "memory-turn", base_revision = current.Revision,
            inventory_ops = Array.Empty<object>(), memory_proposals = proposals });
        var exchange = new InventoryExchange(original, "memory-turn", GameTime: "第3天 傍晚");
        var proposal = InventoryReply.Apply(exchange, Reply(state, new[] { Memory() }));
        var pending = InventoryState.Read(proposal.Snapshot);
        Check(state.Snapshot() == original && pending.Memories.Count == 0 && pending.MemoryProposals.Count == 1,
            "AI只能在候选快照添加待确认记忆，不改当前进度，也不自动采用为事实");
        Check(pending.MemoryProposals[0].Source == "ai" && pending.MemoryProposals[0].Time == "第3天 傍晚" &&
            pending.MemoryPrompt("去集市", "第4天", pending.Scene).Length == 0,
            "记忆建议时间由程序给出，未确认建议绝不发送给AI");
        int id = pending.MemoryProposals[0].Id;
        var adopted = InventoryState.Read(MemoryCommands.Execute(pending, "accept", [id.ToString()])[0]);
        string selected = JsonSerializer.Serialize(adopted.MemoryPrompt("准备去集市", "第4天", adopted.Scene), InventoryState.Json);
        Check(adopted.Memories.Count == 1 && adopted.MemoryProposals.Count == 0 && selected.Contains("次日带艾拉"),
            "玩家明确采用后成为可按相关性检索的长期记忆");
        Check(adopted.Items[0].Quantities["player"] == 2 && adopted.Statuses.Count == 0 && adopted.Scene!.Location == "森林边的小屋",
            "采用历史记忆不改变物品、人物状态或当前地点");
        Check(adopted.MemoryPrompt("我检查猎弓", "第4天", adopted.Scene).Length == 0,
            "无关行动不发送完整历史记忆表");
        var personOnly = InventoryState.Read(adopted.Snapshot());
        personOnly.Memories[0].Keywords = ["艾拉"];
        personOnly.SetScene(new() { Location = "家里", PresentCharacters = ["player", "ella"], Summary = "两人聊天。" });
        Check(personOnly.MemoryPrompt("今天天气怎么样？", "第4天", personOnly.Scene).Length == 0,
            "人物仅仅在场不会让所有涉及她的长期记忆每轮重发");
        var pinned = InventoryState.Read(MemoryCommands.Execute(adopted, "pin", [id.ToString()])[0]);
        Check(pinned.MemoryPrompt("我检查猎弓", "第4天", pinned.Scene).Length == 1, "置顶记忆即使未命中关键词也保留");
        var inactive = InventoryState.Read(MemoryCommands.Execute(pinned, "active", [id.ToString()])[0]);
        Check(inactive.MemoryPrompt("去集市", "第4天", inactive.Scene).Length == 0 && inactive.Memories.Count == 1,
            "失效记忆仍在本地保存，但不再进入AI输入");
        var edited = InventoryState.Read(MemoryCommands.Execute(inactive, "edit", [id.ToString(), "集市约定已经完成。", "第4天", "集市，完成"])[0]);
        Check(edited.Memories[0].Summary == "集市约定已经完成。" && edited.Memories[0].Time == "第4天" && !edited.Memories[0].Active,
            "玩家可以编辑摘要、发生时间与检索关键词，编辑不擅自恢复生效");
        Check(InventoryState.Read(edited.Snapshot()).Snapshot() == edited.Snapshot(), "记忆、候选与编号随存档快照完整往返");
        Check(InventoryState.Read(original).Memories.Count == 0 && InventoryState.Read(original).MemoryProposals.Count == 0,
            "读取较早存档不会残留后来添加的记忆");
        string page = MemoryCommands.Execute(edited, "list", ["0", "0"])[3];
        Check(page.Contains("[1]") && page.Contains("失效") && page.Contains("集市约定"), "管理页按页内编号选择，显示置顶和失效标记");
        Check(MemoryCommands.Execute(edited, "detail", [id.ToString()])[1] == "集市约定已经完成。", "详情提供完整摘要供人工核对");
        var removed = InventoryState.Read(MemoryCommands.Execute(edited, "delete", [id.ToString()])[0]);
        Check(removed.Memories.Count == 0 && edited.Memories.Count == 1, "删除操作只修改返回的候选副本");
        var dismissed = InventoryState.Read(MemoryCommands.Execute(pending, "reject", [id.ToString()])[0]);
        Check(dismissed.MemoryProposals.Count == 0 && dismissed.Memories.Count == 0, "玩家可以拒绝AI建议且不创建已确认记忆");

        var manual = InventoryState.Read(MemoryCommands.Execute(original, 72, ["发现溪边石桥已经塌毁。", "第3天", "石桥，溪边"])[0]);
        Check(manual.Memories[0].Source == "player" && manual.Memories[0].Active && manual.MemoryProposals.Count == 0,
            "玩家可直接手写摘要加入记忆，无需额外AI请求");
        Check(JsonSerializer.Serialize(manual.MemoryPrompt("去溪边", "第4天", manual.Scene), InventoryState.Json).Contains("塌毁"),
            "玩家手写记忆按关键词进入相关行动资料");
        Reject(() => MemoryCommands.Execute(manual, "edit", ["1", "", "第3天", "溪边"]), "空摘要编辑被拒绝");
        Check(manual.Memories[0].Summary.Contains("塌毁"), "失败编辑不会部分改变当前记忆");
        void IgnoreInvalid(object value, string message)
        {
            var ignored = InventoryReply.Apply(exchange, Reply(state, value));
            var saved = InventoryState.Read(ignored.Snapshot);
            Check(saved.MemoryProposals.Count == 0 && saved.NextMemoryId == 1 && ignored.Rationale.Contains("整批建议未保存") &&
                ignored.Story.Contains("答应"), message);
        }
        IgnoreInvalid(new[] { Memory(keywords: new[] { new { value = "集市" } }) }, "对象式关键词整批忽略，保留有效剧情");
        IgnoreInvalid(new[] { Memory(new string('字', 501)) }, "过长AI记忆摘要忽略，不使付费剧情失败");
        IgnoreInvalid(new[] { Memory(), Memory(), Memory(), Memory() }, "超过每轮3条的AI记忆建议整批忽略");
        IgnoreInvalid(new[] { Memory(), Memory("第二个建议", new[] { new { value = "错误对象" } }) }, "第二条格式损坏时第一条也不提前加入或占用编号");
        string validSettlementBadMemory = JsonSerializer.Serialize(new {
            story = "主角交出一把钥匙。", turn_id = "memory-turn", base_revision = state.Revision,
            inventory_ops = new[] { new { op = "transfer_item", item_id = 1, from_owner = "player", to_owner = "ella", quantity = 1, reason = "交给艾拉" } },
            memory_proposals = new[] { new { summary = 42, keywords = new[] { "钥匙" } } } });
        state.Owners["ella"] = "艾拉";
        var independent = InventoryState.Read(InventoryReply.Apply(new(state.Snapshot(), "memory-turn"), validSettlementBadMemory).Snapshot);
        Check(independent.Item(1).Quantities["ella"] == 1 && independent.MemoryProposals.Count == 0,
            "坏记忆建议不阻断已通过校验的物品转交候选");
        state.Owners.Remove("ella");
        Check(state.Snapshot() == original, "记忆解析失败不会提前提交本轮任何变更");
        var duplicate = InventoryState.Read(InventoryReply.Apply(new(manual.Snapshot(), "memory-turn"), Reply(manual,
            new[] { Memory("发现溪边石桥已经塌毁。", new[] { "溪边" }) })).Snapshot);
        Check(duplicate.MemoryProposals.Count == 0, "相同摘要不会反复产生待确认建议");

        var datedProposal = InventoryReply.Apply(exchange, Reply(state, new[] { new {
            summary = "约好第4天与艾拉去集市买盐，尚未履行。", keywords = new[] { "集市", "买盐" }, due_day = 4 } }));
        var datedPending = InventoryState.Read(datedProposal.Snapshot);
        Check(datedPending.MemoryProposals[0].DueDay == 4 && datedPending.MemoryPrompt("整理猎具", "第4天", datedPending.Scene).Length == 0,
            "明确约定日期随候选保存，到期也不会绕过玩家采用步骤");
        int datedId = datedPending.MemoryProposals[0].Id;
        var dated = InventoryState.Read(MemoryCommands.Execute(datedPending, "accept", [datedId.ToString()])[0]);
        Check(dated.MemoryPrompt("整理猎具", "第3天 清晨", dated.Scene).Length == 0 &&
            dated.MemoryPrompt("整理猎具", "时间未知", dated.Scene).Length == 0,
            "未到期或游戏日未知时，不凭摘要或现实日期猜测到期");
        Check(dated.MemoryPrompt("整理猎具", "第 4 天 / 时间0", dated.Scene).Length == 1 &&
            dated.MemoryPrompt("整理猎具", "第5天 / 时间0", dated.Scene).Length == 1 && dated.Memories[0].Active,
            "到期与逾期的已确认约定可主动召回，日期经过不会擅自标记完成");
        var datedEdited = InventoryState.Read(MemoryCommands.Execute(dated, "edit", [datedId.ToString(),
            "约好第4天与艾拉去集市买盐，需要确认是否已经履行。", "第3天 傍晚", "集市，买盐"])[0]);
        var detail = MemoryCommands.Execute(datedEdited, "detail", [datedId.ToString()]);
        Check(detail[2] == "第3天 傍晚" && detail[9] == "4" &&
            MemoryCommands.Execute(datedEdited, "list", ["0", "0"])[3].Contains("约定第4天"),
            "列表与详情显示约定游戏日，编辑保留独立日期且不污染发生时间");
        var datedInactive = InventoryState.Read(MemoryCommands.Execute(dated, "active", [datedId.ToString()])[0]);
        Check(datedInactive.Memories[0].DueDay == 4 && datedInactive.MemoryPrompt("去集市", "第9天", datedInactive.Scene).Length == 0,
            "完成或取消约定后可用现有失效操作停止关键词及到期提醒，原记录仍保留");
        string memoryDirectory = Path.Combine(Path.GetTempPath(), "EraAI-long-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recent = new ConversationStore(memoryDirectory);
            recent.Append("memory_fixture", new("第3天", "明天去买盐", "你与艾拉约好了。"));
            recent.Clear("memory_fixture");
            var restored = InventoryState.Read(dated.Snapshot());
            var request = new AiRequest("场景事实", "我整理猎具。", "memory_fixture", "第4天 时间0", 1024, 0, "",
                Inventory: new(restored.Snapshot(), "after-history-clear", GameTime: "第4天 时间0"));
            string completePrompt = PromptBuilder.WithHistory(PromptBuilder.User(request, recent.Read("memory_fixture")), recent.Read("memory_fixture"));
            using var recalled = JsonDocument.Parse(completePrompt);
            var context = recalled.RootElement.GetProperty("program_context");
            var longTerm = context.GetProperty("long_term_memories");
            Check(recent.Read("memory_fixture").Count == 0 && context.GetProperty("adopted_history").GetArrayLength() == 0 &&
                longTerm.GetArrayLength() == 1 && longTerm[0].GetProperty("summary").GetString()!.Contains("买盐") &&
                longTerm[0].GetProperty("due_day").GetInt32() == 4 && longTerm[0].GetProperty("due").GetBoolean(),
                "采纳后保存读取并清空近期上下文，完整AI请求仍能召回到期约定，无额外API请求");
            Check(restored.Snapshot() == dated.Snapshot(), "检索到期约定不暗中改写状态、日期或存档版本");
        }
        finally { if (Directory.Exists(memoryDirectory)) Directory.Delete(memoryDirectory, true); }
        IgnoreInvalid(new[] { new { summary = "无效日期", keywords = new[] { "约定" }, due_day = -1 } },
            "负数约定日期整批忽略，保留有效剧情与其他结算");
        IgnoreInvalid(new[] { new { summary = "无效日期", keywords = new[] { "约定" }, due_day = 4.5 } },
            "非整数约定日期不截断成游戏日");
        IgnoreInvalid(new[] { new { summary = "无效日期", keywords = new[] { "约定" }, due_day = "明天" } },
            "文字式模糊日期不被程序猜测为明确游戏日");
        IgnoreInvalid(new[] { 42 }, "非对象记忆建议被整批忽略，不使本轮剧情失败");
        Reject(() => InventoryState.Read(dated.Snapshot().Replace("\"due_day\":4", "\"due_day\":0")),
            "损坏的已保存约定日期明确拒绝读取，不静默重置记忆");
        Check(adopted.Memories[0].DueDay == null, "旧版记忆未包含约定日期时仍按原关键词机制检索");
        var dueMany = InventoryState.Read(dated.Snapshot());
        for (int i = 0; i < 9; i++) dueMany.Memories.Add(new NarrativeMemory { Id = dueMany.NextMemoryId++,
            Time = "第3天", Summary = $"第4天应处理的独立约定{i}", Keywords = [$"旧约定{i}"], DueDay = 4 });
        Check(dueMany.MemoryPrompt("我整理猎具", "第4天", dueMany.Scene).Length == 2,
            "同时到期的无关键词约定每轮最多提醒2条，不将整张记忆表发送给AI");
        for (int i = 0; i < 4; i++) dueMany = InventoryState.Read(MemoryCommands.Execute(dueMany, "add", [$"猎具记录{i}", "第3天", "猎具"])[0]);
        Check(dueMany.MemoryPrompt("我整理猎具", "第4天", dueMany.Scene).Length == 6,
            "到期提醒只占有限名额，其余仍可提供与当轮行动相关的历史，总量不超过6条");

        var many = state;
        for (int i = 0; i < 10; i++) many = InventoryState.Read(MemoryCommands.Execute(many, "add", [$"集市记录{i}", "第3天", "集市"])[0]);
        Check(many.MemoryPrompt("去集市", "第4天", many.Scene).Length == 6, "相关记忆每轮最多6条");
        for (int i = 1; i <= 4; i++) many = InventoryState.Read(MemoryCommands.Execute(many, "pin", [i.ToString()])[0]);
        Check(JsonSerializer.Serialize(many.MemoryPrompt("去集市", "第4天", many.Scene), InventoryState.Json).Contains("集市记录0"),
            "置顶条目优先于普通相关记忆");
        Reject(() => MemoryCommands.Execute(many, "pin", ["5"]), "置顶最多4条避免每轮无限增加输入");
        using var doc = JsonDocument.Parse(many.Snapshot());
        string legacy = JsonSerializer.Serialize(doc.RootElement.EnumerateObject().Where(p => p.Name is not ("memories" or "memory_proposals" or "next_memory_id"))
            .ToDictionary(p => p.Name, p => p.Value.Clone()));
        var old = InventoryState.Read(legacy);
        Check(old.Memories.Count == 0 && old.NextMemoryId == 1 && old.Quantity(1) == 2, "旧存档缺少记忆字段时兼容为空表且保留其他数据");
        Reject(() => InventoryState.Read(many.Snapshot().Replace("\"next_memory_id\":11", "\"next_memory_id\":1")), "损坏的记忆编号拒绝读取而非静默清空");
    }
}
