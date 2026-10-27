#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.NativeAI;

// The ERB SAVESTR:61 snapshot is authoritative. No process-wide inventory or sidecar file.
internal sealed class InventoryItem
{
    [JsonRequired]
    public int Id { get; set; }
    [JsonRequired]
    public string Name { get; set; } = "";
    [JsonRequired]
    public string Description { get; set; } = "";
    [JsonRequired]
    public string Type { get; set; } = "";
    [JsonRequired]
    public string Attributes { get; set; } = "";
    [JsonRequired]
    public string Summary { get; set; } = "";
    [JsonRequired]
    public Dictionary<string, int> Quantities { get; set; } = new();
}

internal sealed partial class InventoryState
{
    internal const int MaxQuantity = 1_000_000_000;
    internal const int MaxItems = 512;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public int NextId { get; set; } = 1;
    [JsonRequired]
    public int Revision { get; set; }
    [JsonRequired]
    public string LastTurnId { get; set; } = "";
    [JsonRequired]
    public List<InventoryItem> Items { get; set; } = [];

    public Dictionary<string, string> Owners { get; set; } = new() { ["player"] = "主角", ["world"] = "无主" };

    internal string Snapshot() { Validate(); return JsonSerializer.Serialize(this, Json); }
    internal static InventoryState Read(string snapshot)
    {
        try
        {
            if (snapshot.Length > 2_000_000) throw new JsonException();
            using var doc = JsonDocument.Parse(snapshot);
            InventoryReply.RejectDuplicateProperties(doc.RootElement);
            var state = JsonSerializer.Deserialize<InventoryState>(snapshot, Json) ?? throw new JsonException();
            if (!doc.RootElement.TryGetProperty("owners", out _))
                foreach (string owner in state.Items.SelectMany(i => i.Quantities.Keys).Distinct())
                    state.Owners.TryAdd(owner, owner);
            state.Validate();
            state.NormalizeFatigueStatuses();
            state.Validate();
            return state;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        { throw new InvalidOperationException("存档中的物品或人物状态数据无效，未重置或覆盖。请读取其他存档。", e); }
    }
    private void Validate()
    {
        ValidateStatuses();
        ValidateFatigueHistory();
        ValidateFatiguePoints();
        ValidateProfiles();
        ValidateCharacterImportance();
        ValidateMemories();
        ValidateDynamicCharacters();
        if (Version != 1 || Items == null || Items.Count > MaxItems || NextId < 1 || Revision < 0 || Revision == int.MaxValue ||
            LastTurnId == null || LastTurnId.Length > 64) throw new ArgumentException("物品表版本或编号无效。");
        if (Owners == null || Owners.Count is < 1 or > 256) throw new ArgumentException("物品归属登记无效。");
        foreach (var pair in Owners) { Owner(pair.Key); Text(pair.Value, 80, true); }
        var ids = new HashSet<int>();
        foreach (var item in Items)
        {
            if (item == null || item.Id < 1 || item.Id >= int.MaxValue || !ids.Add(item.Id)) throw new ArgumentException("物品编号无效或重复。");
            Text(item.Name, 80, true); Text(item.Description, 2000, true);
            Text(item.Attributes, 1000, false); Text(item.Summary, 300, false);
            if (item.Type is not ("消耗品" or "普通物品" or "特殊物品")) throw new ArgumentException("物品类型无效。");
            if (item.Quantities == null || item.Quantities.Count is < 1 or > 256) throw new ArgumentException("物品归属无效。");
            foreach (var (owner, quantity) in item.Quantities)
            {
                RequireOwner(owner);
                if (quantity < 0 || quantity > MaxQuantity) throw new ArgumentException("物品数量超出范围。");
            }
        }
        if (Items.Count > 0 && NextId <= Items.Max(i => i.Id)) throw new ArgumentException("物品编号分配进度无效。");
        ValidateContext();
    }
    internal static void Text(string? value, int max, bool required)
    {
        if (value == null || value.Length > max || (required && string.IsNullOrWhiteSpace(value)) ||
            value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("物品文字为空、过长或包含无效字符。");
    }
    internal static void Owner(string owner)
    {
        if (owner == null || !System.Text.RegularExpressions.Regex.IsMatch(owner, "^[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("物品归属编号无效。");
    }
    internal void RequireOwner(string owner)
    {
        Owner(owner);
        if (!Owners.ContainsKey(owner)) throw new ArgumentException("物品归属未在当前剧本中登记。");
    }
    internal string OwnerName(string owner) => Owners.GetValueOrDefault(owner, owner);
    internal void BindOwners(System.Collections.Generic.IReadOnlyDictionary<string, string> owners)
    {
        var merged = owners.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (id, person) in DynamicCharacters)
        {
            if (merged.ContainsKey(id)) throw new InvalidOperationException("剧本归属编号与本局新增人物冲突，未覆盖数据。");
            merged.Add(id, person.Name);
        }
        if (Items.Any(i => i.Quantities.Keys.Any(k => !merged.ContainsKey(k)))) throw new InvalidOperationException("存档物品包含当前剧本未登记的归属，未覆盖数据。");
        Owners = merged;
        Validate();
    }
    internal void AddInitial(int id, string name, string description, string type, string attributes, int quantity, string owner, string summary)
    {
        Owner(owner);
        Owners.TryAdd(owner, owner);
        Items.Add(new() { Id = id, Name = name, Description = description, Type = type, Attributes = attributes,
            Summary = summary, Quantities = new() { [owner] = quantity } });
        NextId = Math.Max(NextId, checked(id + 1));
        Validate();
    }
    internal InventoryItem Item(int id) => Items.Find(i => i.Id == id) ?? throw new ArgumentException("指定物品不存在。");
    internal int Quantity(int id, string[]? owners = null) => Item(id).Quantities.Where(q => owners == null || owners.Contains(q.Key)).Aggregate(0, (sum, q) => checked(sum + q.Value));
    internal void SetQuantity(int id, string owner, int quantity)
    {
        RequireOwner(owner);
        if (quantity < 0 || quantity > MaxQuantity) throw new ArgumentException("物品数量超出范围。");
        Item(id).Quantities = new() { [owner] = quantity };
    }
    internal void Consume(int id, int cost, string[] owners)
    {
        if (cost < 0 || cost > MaxQuantity || owners.Length == 0 || owners.Distinct().Count() != owners.Length) throw new ArgumentException("物品消耗或归属顺序无效。");
        foreach (string owner in owners) RequireOwner(owner);
        var item = Item(id);
        foreach (string owner in owners)
        {
            int used = Math.Min(item.Quantities.GetValueOrDefault(owner), cost);
            item.Quantities[owner] = item.Quantities.GetValueOrDefault(owner) - used;
            cost -= used;
        }
        Revision = checked(Revision + 1);
    }
    internal string Holdings(InventoryItem item) => string.Join(" / ", item.Quantities.Select(q => $"{OwnerName(q.Key)}：{q.Value}"));
    internal string[] Page(int page)
    {
        int pages = Math.Max(1, (Items.Count + 9) / 10);
        page = Math.Clamp(page, 0, pages - 1);
        var result = Enumerable.Repeat("", 12).ToArray();
        int index = 0;
        foreach (var item in Items.OrderBy(i => i.Id).Skip(page * 10).Take(10))
            result[index++] = $"[{item.Id}] {item.Name}    {Holdings(item)}";
        result[10] = pages.ToString(); result[11] = Items.Count.ToString();
        return result;
    }
    internal string Prompt(string player)
    {
        var selected = PromptSelection.Select(this, player, Array.Empty<ConversationTurn>());
        return "本轮相关物品（局部资料，未列出不代表不存在）：" + JsonSerializer.Serialize(
            selected.Select(i => new { id = i.Id, name = i.Name, amounts = i.Quantities,
                description = player.Contains(i.Name, StringComparison.Ordinal) ? i.Description : "" }), Json);
    }
}

internal sealed record InventoryExchange(string Snapshot, string TurnId, ScenarioActionOutcome? Action = null, string ScenarioId = "", string Instruction = "", string GameTime = "", PromptSelectionRules? SelectionRules = null, ItemActionSelection? SelectedItem = null, IReadOnlyList<int>? SelectedItems = null, IReadOnlyDictionary<int,int>? SelectedAmounts = null, ScenarioDefinition? Scenario = null);
internal sealed record InventoryReplyResult(string Story, string Snapshot, string Changes, string Suggestions = "[]", bool RequiresApproval = false, string Rationale = "", string Warning = "");

internal static partial class InventoryReply
{
    internal static string Instruction(InventoryExchange context)
    {
        return """
返回一个完整JSON对象。以下是字段格式示例，不是当前剧情或操作：
{"story":"给玩家的完整剧情","turn_id":"复制本轮编号","base_revision":0,"inventory_ops":[],"state_ops":[],"suggestions":[],"effort":"none"}
turn_id和base_revision必须复制program_context中的实际值，示例中的编号和0不能照抄。story为完整剧情字符串，不是摘要；suggestions为0至3条简短后续建议字符串。无变化的操作数组为[]，不要添加代码围栏。
本轮已发生的获得、消耗、付款、收货、转交及伤势处理与操作逐项对应；计划、报价、假设不结算，也不得留到下轮补旧账。
每项op必须是下列示例中的操作名字符串。除item_id/status_id/delta/quantity外，物品及状态操作中的所有字段都为字符串；attributes和effects无内容时用空字符串，不能用对象、数组或null。归属和人物编号取program_context登记值，示例中的编号/归属仅展示格式。
增减已有物品：{"op":"change_quantity","item_id":1,"owner":"归属编号","delta":-1,"reason":"本轮消耗依据"}
创建新物品：{"op":"create_item","name":"名称","description":"物品描述","type":"普通物品","attributes":"材质：布；用途：包扎","quantity":2,"owner":"归属编号","reason":"本轮获得依据"}
转交已有物品：{"op":"transfer_item","item_id":1,"from_owner":"原归属编号","to_owner":"目标归属编号","quantity":1,"reason":"本轮转交依据"}
item_id/status_id/delta/quantity为整数；delta非零、quantity为正数，不能透支。type只用消耗品、普通物品、特殊物品。已有同名物品用增减操作，不重复创建或修改定义。新物品编号从program_context.next_item_id依次分配，可在同轮后续操作引用。
新物品description只写单件或同类物品的材质、外观、用途，不写当前持有几件、归谁所有；实时数量和归属仅在quantity/owner记录，不能写“两条布条”等会随数量变化失效的描述。
""" + StatusInstruction(context) + EffortInstruction(context) + CharacterInstruction() + MemoryInstruction() + """
场景确有变化时可增加scene_update，完整格式：{"location":"当前地点","present_characters":["已登记人物编号"],"summary":"当前场景短摘要","reason":"本轮变化依据"}。
location/summary/reason为字符串，present_characters只含人物编号字符串，summary最多600字符。无变化时省略scene_update，不返回null；不能借此改时间、物品或伤势，也不回放初始开场。
玩家实际移动只由地图路线执行与计费；scene_update只能更新当前已确认地点内的情况，不能改变玩家所在地、移走player或清除其坐标。玩家提出去别处时可以回应准备与目的，但不叙述已经到达，建议使用移动选项画路线。
scene_update可另含character_locations和conditions，例如："character_locations":{"人物编号":"明确移动到的场外地点"},"conditions":{"门锁":"已锁","壁炉":"熄灭"}。人物所在地只更新本轮已确定移动的人物；未更新的场外人物留在原地，当前地点已知有人时须列入present_characters，离开须给明确去向。conditions是本轮location的设施状态文字键值表（最多16项），按键合并，省略保留；设施状态改变也需玩家确认。场景卡只是地点资料，不能使人物自动到场。
若program_context含program_action，按程序已确定结果续写，inventory_ops和state_ops都为[]，省略scene_update，结算及结束场景由程序处理。
""";
    }

    private static string EffortInstruction(InventoryExchange context) => InventoryState.Read(context.Snapshot).FatiguePoints.HasValue ? """
本轮必须填写effort字段：none、light、moderate、heavy，表示玩家实际完成活动的体力消耗，程序换算疲劳点。交谈、询价、咨询、意图、否定和假设用none；仅说自己累或不累不能直接改疲劳。不要猜疲劳点或用state_ops修改主角疲劳。program_action存在时疲劳已由程序确定，effort用none，不能重复计算。
""" : "";

    private static ScenarioDefinition FatigueScenario(InventoryExchange context, InventoryState state) => context.Scenario ??
        new ScenarioDefinition { FatigueRules = new FatigueRules { Enabled = state.FatiguePoints.HasValue } };

    internal static InventoryReplyResult Apply(InventoryExchange context, string text)
    {
        try
        {
            if (text.Length > 200_000) throw new ArgumentException("回复过长。");
            // Accept a single complete JSON fence, but never loose prose followed by hidden commands.
            text = text.Trim();
            if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && text.EndsWith("```")) text = text[7..^3].Trim();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            RejectDuplicateProperties(root);
            string suggestions = "[]", warning = "";
            var allowed = new List<string> { "story", "turn_id", "base_revision", "inventory_ops" };
            if (root.TryGetProperty("state_ops", out _)) allowed.Add("state_ops");
            if (root.TryGetProperty("suggestions", out _)) allowed.Add("suggestions");
            if (root.TryGetProperty("scene_update", out _)) allowed.Add("scene_update");
            if (root.TryGetProperty("memory_proposals", out _)) allowed.Add("memory_proposals");
            if (root.TryGetProperty("character_ops", out _)) allowed.Add("character_ops");
            if (root.TryGetProperty("effort", out _)) allowed.Add("effort");
            Fields(root, allowed.ToArray());
            if (root.TryGetProperty("suggestions", out var suggested))
            {
                try { suggestions = ActionRules.Suggestions(suggested); }
                catch (ArgumentException) { warning = "下一步建议格式不合适，已略过，不影响本轮剧情和结算。"; }
            }
            string story = String(root, "story", 100_000);
            var state = InventoryState.Read(context.Snapshot);
            var fatigueScenario = FatigueScenario(context, state);
            state.BindFatigue(fatigueScenario);
            var itemPlan = context.SelectedItem is { } selection ? ItemActions.Prepare(state, selection, context.GameTime, context.Scenario) : null;
            if (itemPlan != null) ItemActions.ValidateModelOperations(itemPlan, root);
            if (String(root, "turn_id", 64) != context.TurnId || Int(root, "base_revision") != state.Revision || state.LastTurnId == context.TurnId)
                throw new ArgumentException("回复与当前回合不匹配或已结算。");
            var ops = root.GetProperty("inventory_ops");
            if (ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() > 32) throw new ArgumentException("物品操作列表无效。");
            if (context.Action != null && ops.GetArrayLength() != 0)
                throw new ArgumentException("本次行动由程序结算，模型不得追加物品操作。");
            int firstNewId = state.NextId;
            var changes = new List<string>();
            var rationale = new List<string>();
            int characterCount = ApplyCharacters(context, root, state, changes, rationale);
            var selectedDebits = new Dictionary<int, long>();
            void CheckSelectionAmount(int id, long count)
            {
                if (count <= 0 || context.SelectedAmounts == null || !context.SelectedAmounts.TryGetValue(id, out int selected)) return;
                long debit = selectedDebits.GetValueOrDefault(id) + count;
                if (debit > selected) throw new ArgumentException("本轮使用或转交数量超过已选数量，请调整选择后重试。");
                selectedDebits[id] = debit;
            }
            foreach (var op in ops.EnumerateArray())
            {
                string kind = String(op, "op", 32);
                rationale.Add(String(op, "reason", 300));
                if (kind == "change_quantity")
                {
                    Fields(op, "op", "item_id", "owner", "delta", "reason");
                    var item = state.Item(Int(op, "item_id"));
                    string owner = String(op, "owner", 64); state.RequireOwner(owner);
                    int delta = Int(op, "delta");
                    if (delta < 0) CheckSelectionAmount(item.Id, -(long)delta);
                    long next = (long)item.Quantities.GetValueOrDefault(owner) + delta;
                    if (delta == 0 || next < 0 || next > InventoryState.MaxQuantity) throw new ArgumentException("物品数量不足或变更数量无效。");
                    item.Quantities[owner] = (int)next;
                    changes.Add($"{state.OwnerName(owner)}的{item.Name} {(delta > 0 ? "+" : "")}{delta}");
                }
                else if (kind == "create_item")
                {
                    Fields(op, "op", "name", "description", "type", "attributes", "quantity", "owner", "reason");
                    string name = String(op, "name", 80).Trim();
                    if (state.Items.Any(i => string.Equals(i.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("已有同名物品，应修改其数量。");
                    int quantity = Int(op, "quantity");
                    if (quantity <= 0 || quantity > InventoryState.MaxQuantity || state.Items.Count >= InventoryState.MaxItems)
                        throw new ArgumentException("新物品数量或记录数超出范围。");
                    string owner = String(op, "owner", 64);
                    state.RequireOwner(owner);
                    state.AddInitial(state.NextId, name, String(op, "description", 2000), String(op, "type", 16),
                        Attributes(op), quantity, owner, "");
                    changes.Add($"{state.OwnerName(owner)}获得新物品：{name} ×{quantity}");
                    rationale.Add($"新物品资料：{String(op, "description", 2000)} / {String(op, "type", 16)} / {Attributes(op)}");
                }
                else if (kind == "transfer_item")
                {
                    Fields(op, "op", "item_id", "from_owner", "to_owner", "quantity", "reason");
                    var item = state.Item(Int(op, "item_id"));
                    string from = String(op, "from_owner", 64), to = String(op, "to_owner", 64);
                    state.RequireOwner(from); state.RequireOwner(to);
                    int quantity = Int(op, "quantity");
                    long target = (long)item.Quantities.GetValueOrDefault(to) + quantity;
                    CheckSelectionAmount(item.Id, quantity);
                    if (from == to || quantity <= 0 || item.Quantities.GetValueOrDefault(from) < quantity || target > InventoryState.MaxQuantity)
                        throw new ArgumentException("转移物品数量或归属无效。");
                    item.Quantities[from] -= quantity;
                    item.Quantities[to] = (int)target;
                    changes.Add($"{item.Name} ×{quantity}：{state.OwnerName(from)} → {state.OwnerName(to)}");
                }
                else throw new ArgumentException("包含不支持的物品操作。");
            }
            int statusCount = ApplyStatuses(context, root, state, changes, rationale, ops.GetArrayLength());
            bool sceneChanged = ApplyScene(context, root, state, changes, rationale);
            int memoryNoticeStart = rationale.Count;
            ApplyMemories(context, root, state, changes, rationale);
            foreach (string notice in rationale.Skip(memoryNoticeStart))
                if (notice.StartsWith("本轮记忆建议格式无效", StringComparison.Ordinal) || notice.StartsWith("待确认记忆已满", StringComparison.Ordinal))
                    warning += (warning.Length > 0 ? "\n" : "") + notice;
            if (itemPlan != null) ItemActions.Apply(itemPlan, state, changes, rationale);
            bool hasEffort = root.TryGetProperty("effort", out _);
            if (state.FatiguePoints.HasValue && context.Action == null && !hasEffort)
                throw new ArgumentException("回复缺少活动强度effort，本轮疲劳与其他变化均未采用。");
            string effort = hasEffort ? String(root, "effort", 16) : "none";
            if (effort is not ("none" or "light" or "moderate" or "heavy")) throw new ArgumentException("活动强度须为none、light、moderate或heavy。");
            bool fatigueChanged = false;
            // Only return a new snapshot after the entire batch passes. Original snapshot remains intact on error.
            if (context.Action is { } action)
            {
                state.RequireOwner(action.Owner);
                var item = state.Item(action.ItemId);
                int current = item.Quantities.GetValueOrDefault(action.Owner);
                if ((long)current + action.Quantity > InventoryState.MaxQuantity) throw new ArgumentException("行动收益超出物品数量范围。");
                item.Quantities[action.Owner] = current + action.Quantity;
                changes.Insert(0, action.Summary);
                string fatigueChange = FatigueSystem.Change(state, fatigueScenario, action.FatigueCost, "狩猎", context.GameTime);
                if (fatigueChange.Length > 0) changes.Add(fatigueChange);
                state.SetScene(state.ResolveActionScene(action.EndScene));
                story = ActionNarrative.Check(story, action, out bool replaced);
                if (replaced) warning += "行动剧情中的数量与程序结果不一致，已使用对应的简短记录。";
            }
            else
            {
                string fatigueChange = FatigueSystem.ApplyEffort(state, fatigueScenario, effort, context.GameTime);
                fatigueChanged = fatigueChange.Length > 0;
                if (fatigueChanged) changes.Add(fatigueChange);
            }
            CaptureItemFocus(state, ops, firstNewId, context.Action);
            if (context.SelectedItem is { } focus) state.ContextItemIds = state.ContextItemIds.Append(focus.ItemId).Distinct().TakeLast(32).ToList();
            state.LastTurnId = context.TurnId;
            state.Revision = checked(state.Revision + 1);
            return new(story, state.Snapshot(), string.Join("\n", changes), suggestions, context.Action == null && (ops.GetArrayLength() > 0 || statusCount > 0 || characterCount > 0 || sceneChanged || fatigueChanged || itemPlan?.Cost > 0), string.Join("\n", rationale), warning);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            string reason = e switch
            {
                JsonException json when json.Message == "Duplicate property" => "模型回复包含重复字段。",
                JsonException => "模型回复的JSON语法无效或内容不完整。",
                KeyNotFoundException => "模型回复缺少必要字段。",
                OverflowException => "模型提出的数量或编号超出范围。",
                ArgumentException argument => argument.Message,
                _ => "当前状态或结算内容无法校验。",
            };
            throw new InvalidOperationException("本轮未采用：" + reason + " 物品、人物状态和时间未变。请重试。", e);
        }
    }
    internal static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) RejectDuplicateProperties(value);
    }
    private static void Fields(JsonElement node, params string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new ArgumentException("JSON记录需要对象格式。");
        var missing = names.Where(name => !node.TryGetProperty(name, out _)).ToArray();
        if (missing.Length > 0) throw new ArgumentException("缺少必要字段：" + string.Join("、", missing) + "。");
        if (node.EnumerateObject().Count() != names.Length || node.EnumerateObject().Any(p => !names.Contains(p.Name)))
            throw new ArgumentException("JSON记录包含不支持的额外字段。");
    }
    private static string String(JsonElement node, string name, int max, bool required = true)
    {
        var value = node.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"字段 {name} 需要文字，实际为{KindName(value.ValueKind)}。");
        string text = value.GetString()!;
        try { InventoryState.Text(text, max, required); }
        catch (ArgumentException) { throw new ArgumentException($"字段 {name} 为空、超过{max}字符或包含无效控制字符。"); }
        return text;
    }
    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "对象", JsonValueKind.Array => "数组", JsonValueKind.Number => "数字",
        JsonValueKind.Null => "null", JsonValueKind.True or JsonValueKind.False => "布尔值", _ => "非文字数据",
    };
    private static string Attributes(JsonElement operation)
    {
        var value = operation.GetProperty("attributes");
        if (value.ValueKind == JsonValueKind.String) return String(operation, "attributes", 1000, false);
        // Some providers return a small key/value attribute table. Preserve it as inert text;
        // never coerce quantities, IDs, ownership or operations, and reject nested structures.
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > 16)
            throw new ArgumentException("字段 attributes 需要文字或简单属性表。");
        var fields = new List<string>();
        foreach (var pair in value.EnumerateObject())
        {
            InventoryState.Text(pair.Name, 80, true);
            if (pair.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("字段 attributes 的属性值必须是文字、数字或布尔值，不能嵌套对象或数组。");
            fields.Add(pair.Name + "：" + (pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() : pair.Value.GetRawText()));
        }
        string text = string.Join("；", fields);
        InventoryState.Text(text, 1000, false);
        return text;
    }
    private static int Int(JsonElement node, string name)
    {
        var value = node.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number)) throw new ArgumentException($"字段 {name} 需要整数。");
        return number;
    }
}
