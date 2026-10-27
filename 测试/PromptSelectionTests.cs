using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class PromptSelectionTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }

    internal static void Run()
    {
        var state = new InventoryState();
        state.AddInitial(1, "复合弓", "用于射箭", "普通物品", "", 1, "player", "");
        state.AddInitial(2, "箭矢", "配合弓使用", "消耗品", "", 3, "player", "");
        state.AddInitial(3, "干净布条", "曾经持有的材料", "消耗品", "", 0, "player", "");
        state.AddInitial(4, "铜钥匙", "开锁", "普通物品", "", 2, "player", "");
        state.AddInitial(5, "ring", "一枚戒指", "普通物品", "", 1, "player", "");
        state.ContextItemIds = [3];
        var rules = new PromptSelectionRules
        {
            ItemAliases = new() { [1] = ["弓"], [2] = ["箭", "弓箭"], [3] = ["布条", "布带"], [4] = ["门钥匙"] },
            RelatedItems = new() { [1] = [2], [2] = [1] },
            ActionItems = new() { ["狩猎"] = [1], ["开门"] = [4] },
        };
        int[] Select(string text, IReadOnlyList<ConversationTurn>? recent = null) =>
            PromptSelection.Select(state, text, recent ?? [], rules).Select(item => item.Id).ToArray();

        Check(Select("聊聊今天的天气").Length == 0, "普通聊天不发送完整物品表或最近变更物品");
        Check(Select("我拿起弓").SequenceEqual([1, 2]), "中文别名匹配当前物品并携带弓箭依赖，循环关联不会重复");
        rules.ActionItems["买"] = [4];
        Check(Select("我买一个").SequenceEqual([4]), "剧本可用单字中文动作触发关联物品，覆盖自然交易说法");
        Check(Select("我准备出去狩猎").SequenceEqual([1, 2]), "动作关键词由剧本配置，自动带上行动依赖");
        Check(Select("我拿着门钥匙").SequenceEqual([4]), "物品别名允许使用玩家自然说法");
        Check(Select("又找到了两条布带").SequenceEqual([3]), "零数量已有物品仍给出原编号，避免重复创建");
        Check(Select("把它收好").SequenceEqual([3]), "代词承接最近已提交的物品焦点");
        Check(Select("其它事情先不管").Length == 0 && Select("讨论其它颜色").Length == 0,
            "其它等普通词中的它不会误触发物品代词承接");
        Check(Select("这是 spring season").Length == 0 && Select("我检查 RING").SequenceEqual([5]),
            "英文名称忽略大小写并保留词边界，不在普通单词内部误匹配");

        ConversationTurn[] recent = [
            new("早些时候", "看看戒指 ring", "你端详戒指。"),
            new("刚才", "我把门钥匙放好", "铜钥匙仍在手边。"),
            new("现在", "整理干净布条", "你将布条叠好。"),
        ];
        Check(Select("它们还能用吗", recent).SequenceEqual([3, 4]), "最近两轮已采纳对话提供指代候选，不继续携带更早物品");
        Check(Select("今天晴天了吗", recent).Length == 0, "切换到无关话题后不携带上一轮物品清单");
        Check(Select("我想查看全部物品").SequenceEqual([1, 2, 3, 4, 5]), "明确要求完整物品表时包含零数量记录");
        Check(Select("不用查看全部物品，先聊聊").Length == 0, "否定完整物品请求不会导致发送全部库存");

        var empty = new InventoryState();
        var newItemContext = new InventoryExchange(empty.Snapshot(), "dynamic-short-name");
        string newItemReply = JsonSerializer.Serialize(new {
            story = "你找到两条干净布条，把它们收好。", turn_id = newItemContext.TurnId, base_revision = empty.Revision,
            inventory_ops = new[] { new { op = "create_item", name = "干净布条", description = "游戏中刚刚找到的干净布条", type = "消耗品",
                attributes = "用于包扎", quantity = 2, owner = "player", reason = "本轮找到两条布条" } }, state_ops = Array.Empty<object>(),
        });
        var generated = InventoryState.Read(InventoryReply.Apply(newItemContext, newItemReply).Snapshot);
        generated.ContextItemIds = [];
        Check(PromptSelection.Select(generated, "我用一条布条帮我包扎", []).Select(item => item.Id).SequenceEqual([1]),
            "动态新增物品没有预设别名、历史或代词时，中文名称短称仍能选出原编号");
        generated.AddInitial(2, "旧布条", "另一种布条", "消耗品", "", 1, "player", "");
        Check(PromptSelection.Select(generated, "我看看布条", []).Select(item => item.Id).SequenceEqual([1, 2]),
            "中文短称对应多个物品时保留所有候选，不擅自猜编号");
        generated.AddInitial(3, "普通物品", "描述尚不明确的物品", "普通物品", "", 1, "player", "");
        Check(PromptSelection.Select(generated, "这里要如何布置", []).Count == 0 &&
            PromptSelection.Select(generated, "先说说物品这个词的意思", []).Count == 0,
            "不从名称生成单字或物品等泛称，避免无关普通词触发选择");

        string original = state.Snapshot();
        string rulesBefore = JsonSerializer.Serialize(rules);
        _ = Select("继续刚才的整理", recent);
        Check(state.Snapshot() == original && JsonSerializer.Serialize(rules) == rulesBefore,
            "选择相关上下文不修改本地数量、回合、焦点或剧本规则");
        rules.ItemAliases[1] = ["共同称呼"];
        rules.ItemAliases[4] = ["共同称呼"];
        Check(Select("我看看共同称呼").SequenceEqual([1, 2, 4]), "别名存在歧义时提供全部候选，不擅自猜物品编号");
        rules.RelatedItems[4] = [999];
        rules.ActionItems["寻找"] = [999];
        Check(Select("寻找").Length == 0 && Select("铜钥匙").SequenceEqual([4]),
            "尚未存在的关联编号不会生成物品或伪造事实");

        foreach (var invalid in new PromptSelectionRules[] {
            new() { ItemAliases = new() { [0] = ["错编号"] } },
            new() { ItemAliases = new() { [1] = ["重复", "重复"] } },
            new() { ItemAliases = new() { [1] = [new string('字', 81)] } },
            new() { ItemAliases = new() { [1] = ["换\n行"] } },
            new() { RelatedItems = new() { [1] = [-1] } },
            new() { RelatedItems = new() { [1] = [2, 2] } },
            new() { ActionItems = new() { ["a"] = [1] } },
            new() { ActionItems = new() { ["打开"] = [] } },
        })
        {
            bool rejected = false;
            try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "拒绝无效上下文别名、关联或行动配置");
        }

        // A selection is never an authority to create an item: full-state validation remains in Apply.
        var context = new InventoryExchange(state.Snapshot(), "selection-duplicate");
        string duplicate = JsonSerializer.Serialize(new {
            story = "本轮又找到一枚铜钥匙。", turn_id = context.TurnId, base_revision = state.Revision,
            inventory_ops = new[] { new { op = "create_item", name = "铜钥匙", description = "重复定义", type = "普通物品",
                attributes = "", quantity = 1, owner = "player", reason = "找到" } }, state_ops = Array.Empty<object>(),
        });
        bool duplicateRejected = false;
        try { InventoryReply.Apply(context, duplicate); } catch (InvalidOperationException) { duplicateRejected = true; }
        Check(duplicateRejected && state.Snapshot() == original, "未进入本轮上下文的同名物品仍由完整本地物品表拒绝重复创建");
    }
}
