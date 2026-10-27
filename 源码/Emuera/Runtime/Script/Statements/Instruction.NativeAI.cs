using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.NativeDisplay;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.GameProc.Function;

internal sealed partial class FunctionIdentifier
{
    private sealed class NativeAIInstruction : AInstruction
    {
        private readonly FunctionCode code;
        public NativeAIInstruction(FunctionCode code)
        {
            this.code = code;
            flag = EXTENDED;
            ArgBuilder = code switch
            {
                FunctionCode.AISTART => ArgumentParser.GetNormalArgumentBuilder("SSSSIIS", 7),
                FunctionCode.AIMEMORY or FunctionCode.AICLEAR => ArgumentParser.GetArgumentBuilder(FunctionArgType.STR_EXPRESSION),
                FunctionCode.AICONNECTION or FunctionCode.AIGENERATION => ArgumentParser.GetArgumentBuilder(FunctionArgType.INT_EXPRESSION),
                _ => ArgumentParser.GetArgumentBuilder(FunctionArgType.VOID),
            };
        }
        public override void DoInstruction(ExpressionMediator exm, InstructionLine func, ProcessState state)
        {
            try
            {
                switch (code)
                {
                    case FunctionCode.AIGENERATION:
                        ConfigureGeneration(exm, (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm));
                        break;
                    case FunctionCode.AICONNECTION:
                        ConfigureConnection(exm, (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm));
                        break;
                    case FunctionCode.AISTART:
                        var args = ((ExpressionsArgument)func.Argument).ArgumentArray;
                        AiRuntime.Start(new AiRequest(args[0].GetStrValue(exm), args[1].GetStrValue(exm), args[2].GetStrValue(exm), args[3].GetStrValue(exm),
                            (int)Math.Clamp(args[4].GetIntValue(exm), 1, int.MaxValue), (int)Math.Clamp(args[5].GetIntValue(exm), 0, 8192), args[6].GetStrValue(exm)));
                        if (AiRuntime.D20Presentation is { } dice) exm.Console.Window.BeginD20Animation(dice.Roll, dice.Summary);
                        exm.VEvaluator.RESULT = 0;
                        break;
                    case FunctionCode.AIPOLL:
                        int poll = AiRuntime.Poll(out var response);
                        if (poll != 0 && !exm.Console.Window.FinishD20Animation(poll > 0))
                        { exm.VEvaluator.RESULT = 0; exm.VEvaluator.RESULTS = ""; break; }
                        exm.VEvaluator.RESULT = poll;
                        exm.VEvaluator.RESULTS = response;
                        exm.VEvaluator.RESULTS_ARRAY[1] = AiRuntime.LastWarning;
                        exm.VEvaluator.RESULTS_ARRAY[2] = AiRuntime.LastInventory;
                        exm.VEvaluator.RESULTS_ARRAY[3] = AiRuntime.LastInventoryChanges;
                        exm.VEvaluator.RESULTS_ARRAY[4] = AiRuntime.LastSuggestions;
                        exm.VEvaluator.RESULTS_ARRAY[5] = AiRuntime.LastRequiresApproval ? "1" : "0";
                        exm.VEvaluator.RESULTS_ARRAY[6] = AiRuntime.LastRationale;
                        break;
                    case FunctionCode.AICANCEL:
                        AiRuntime.Cancel();
                        break;
                    case FunctionCode.AICONFIG:
                        AiRuntime.Configure();
                        break;
                    case FunctionCode.AIINFO:
                        var settings = AiRuntime.Settings.Load();
                        exm.VEvaluator.RESULTS = settings.Model.Length == 0 ? "尚未设置" : settings.Model;
                        exm.VEvaluator.RESULTS_ARRAY[1] = settings.ModeName;
                        break;
                    case FunctionCode.AIMEMORY:
                        exm.VEvaluator.RESULTS = DisplayMemory(((ExpressionArgument)func.Argument).Term.GetStrValue(exm));
                        exm.VEvaluator.RESULT = 1;
                        break;
                    case FunctionCode.AICLEAR:
                        if (AiRuntime.Busy) throw new InvalidOperationException("请等待当前对话完成后再清空记忆。");
                        AiRuntime.Memory.Clear(((ExpressionArgument)func.Argument).Term.GetStrValue(exm));
                        exm.VEvaluator.RESULT = 1;
                        break;
                }
            }
            catch (Exception e)
            {
                exm.VEvaluator.RESULT = -1;
                exm.VEvaluator.RESULTS = e is ArgumentException or InvalidOperationException ? e.Message : "AI 操作失败，请检查配置或文件权限。";
            }
        }
        private static void ConfigureGeneration(ExpressionMediator exm, int field)
        {
            var connection = AiRuntime.Settings.Load();
            var current = AiRuntime.Generation.Load(connection);
            var limits = AiRuntime.GetModelLimits(connection);
            if (field == 0)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                string[] values = [current.PresetName, current.OutputText, current.ContextText(limits),
                    current.HistoryTurns.ToString(culture), current.Temperature.ToString("0.############################", culture), current.TopP.ToString("0.############################", culture),
                    current.FrequencyPenalty.ToString("0.############################", culture), current.PresencePenalty.ToString("0.############################", culture), current.ThinkingName,
                    GenerationPolicy.Notice(connection, current), limits.Description, current.LengthName, current.ContextSummary(limits), AiRuntime.LimitsStatus(connection),
                    GenerationPolicy.SamplingStatus(connection, current), GenerationPolicy.SamplingStatus(connection, current, true)];
                for (int i = 0; i < values.Length; i++) exm.VEvaluator.RESULTS_ARRAY[i] = values[i];
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (AiRuntime.Busy) throw new InvalidOperationException("请等待当前请求完成后再修改生成参数。");
            string input = exm.VEvaluator.RESULTS.Trim();
            if (input.Length == 0) { exm.VEvaluator.RESULT = 0; return; }
            var next = current.Edit(field, input);
            limits.Validate(next, field);
            AiRuntime.Generation.Save(next, connection);
            exm.VEvaluator.RESULTS = "";
            exm.VEvaluator.RESULT = 1;
        }
        private static string DisplayMemory(string id)
        {
            var definition = AiRuntime.Catalog.Entries.Find(e => e.Id == id);
            return AiRuntime.Memory.Display(id, definition?.ReplyLabel ?? "回应");
        }
        // Generic snapshot services. Item IDs, owners, dice and narratives are supplied by the scenario.
        private static void ConfigureInventory(ExpressionMediator exm, int action)
        {
            var values = exm.VEvaluator.RESULTS_ARRAY;
            var scenario = AiRuntime.ActiveScenario;
            if (action == 95)
            {
                exm.Console.PrintBuffer.Append(new TimeSlotsPart(int.Parse(values[0]), int.Parse(values[1])));
                exm.VEvaluator.RESULT = 1; return;
            }
            if (action == 30)
            {
                var empty = new InventoryState();
                if (scenario != null) { empty.BindOwners(scenario.Owners); empty.BindCharacters(scenario.Characters); empty.BindWorld(scenario); }
                exm.VEvaluator.RESULTS = empty.Snapshot(); exm.VEvaluator.RESULT = 1; return;
            }
            if (action == 45)
            {
                exm.VEvaluator.RESULTS = scenario?.Parameter(values[0]).ToString() ?? throw new InvalidOperationException("请先选择剧本。");
                exm.VEvaluator.RESULT = 1; return;
            }
            if (action == 43)
            {
                var suggested = ActionRules.SuggestionList(values[0], scenario?.DefaultSuggestions);
                for (int i = 0; i < 3; i++) values[i] = i < suggested.Length ? suggested[i] : "";
                exm.VEvaluator.RESULT = suggested.Length; return;
            }
            if (action == 44) { exm.VEvaluator.RESULT = ActionRules.CommonAction(values[0], scenario); return; }
            var inventory = InventoryState.Read(values[0]);
            if (scenario != null) { inventory.BindOwners(scenario.Owners); inventory.BindCharacters(scenario.Characters); inventory.BindWorld(scenario); }
            switch (action)
            {
                case 31:
                    inventory.RequireOwner(values[7]);
                    inventory.AddInitial(int.Parse(values[1]), values[2], values[3], values[4], values[5], int.Parse(values[6]), values[7], values[8]);
                    exm.VEvaluator.RESULTS = inventory.Snapshot(); break;
                case 32:
                    int itemId = int.Parse(values[1]);
                    string[] owners = string.IsNullOrEmpty(values[4]) ? null : values[4].Split(',');
                    values[1] = inventory.Quantity(itemId, owners).ToString();
                    values[2] = inventory.Items.Count.ToString(); values[3] = inventory.Revision.ToString();
                    exm.VEvaluator.RESULTS = inventory.Snapshot(); break;
                case 33:
                    inventory.SetQuantity(int.Parse(values[2]), values[3], int.Parse(values[1]));
                    exm.VEvaluator.RESULTS = inventory.Snapshot(); break;
                case 34:
                    string player = values[1];
                    AiRuntime.PrepareInventory(inventory.Snapshot());
                    exm.VEvaluator.RESULTS = inventory.Prompt(player); break;
                case 35:
                    var next = InventoryState.Read(values[1]);
                    if (scenario != null) { next.BindOwners(scenario.Owners); next.BindCharacters(scenario.Characters); next.BindWorld(scenario); }
                    if (next.Revision != inventory.Revision + 1 || string.IsNullOrEmpty(next.LastTurnId) || next.LastTurnId == inventory.LastTurnId)
                        throw new InvalidOperationException("物品结算已经处理或不属于当前进度，未覆盖库存。");
                    exm.VEvaluator.RESULTS = next.Snapshot(); break;
                case 36:
                    int id = int.Parse(values[2]);
                    inventory.Consume(id, int.Parse(values[1]), values[3].Split(','));
                    values[1] = inventory.Quantity(id, values[3].Split(',')).ToString();
                    exm.VEvaluator.RESULTS = inventory.Snapshot(); break;
                case 37:
                    var item = inventory.Item(int.Parse(values[1]));
                    values[0] = item.Name; values[1] = item.Description; values[2] = item.Type;
                    values[3] = item.Attributes; values[4] = inventory.Holdings(item); values[5] = item.Id.ToString(); break;
                case 40:
                    ScenarioActions.EnsureLocation(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), "hunt");
                    var outcome = FatigueDice.Resolve(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), values[2], int.Parse(values[1]));
                    values[0] = outcome.Summary; values[1] = outcome.Quantity.ToString(); values[2] = outcome.Story; values[3] = outcome.Instruction; break;
                case 41:
                    ScenarioActions.EnsureLocation(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), "hunt");
                    AiRuntime.PrepareInventory(inventory.Snapshot(), FatigueDice.Resolve(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), values[2], int.Parse(values[1]))); break;
                case 42:
                    ScenarioActions.EnsureLocation(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), "hunt");
                    var result = FatigueDice.Resolve(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), values[2], int.Parse(values[1]));
                    var exchange = new InventoryExchange(inventory.Snapshot(), Guid.NewGuid().ToString("N"), result, scenario.Id, scenario.InventoryInstruction, Scenario: scenario);
                    string reply = System.Text.Json.JsonSerializer.Serialize(new { story = result.Story, turn_id = exchange.TurnId,
                        base_revision = inventory.Revision, inventory_ops = Array.Empty<object>() });
                    var fallback = InventoryReply.Apply(exchange, reply);
                    values[0] = fallback.Snapshot; values[1] = fallback.Story; values[2] = fallback.Changes; break;
                case 52:
                    var statusLines = inventory.StatusPage(int.Parse(values[1]));
                    for (int i = 0; i < statusLines.Length; i++) values[i] = statusLines[i]; break;
                case 57:
                    if (scenario == null) throw new InvalidOperationException("请先选择剧本。");
                    inventory.SetScene(scenario.InitialScene);
                    values[0] = inventory.Snapshot(); break;
                case 55:
                    values[0] = inventory.Scene?.Location ?? "未记录（以最近剧情为准）";
                    values[1] = inventory.Scene?.Summary ?? ""; break;
                case 56:
                    if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前对话。");
                    if (scenario == null) throw new InvalidOperationException("请先选择剧本。");
                    if (values[1] != "rest") throw new ArgumentException("未知本地行动。");
                    var legacySleep = ScenarioActions.CompleteLocal(inventory, scenario, "sleep");
                    values[0] = legacySleep.Snapshot; values[1] = legacySleep.Narrative; break;
                case 53:
                    var status = inventory.Status(int.Parse(values[1]));
                    values[0] = status.Name; values[1] = inventory.Characters[status.Character];
                    values[2] = status.Category; values[3] = status.Severity; values[4] = inventory.FatigueDetail(status);
                    values[5] = status.Effects; values[6] = status.CreatedAt; values[7] = status.UpdatedAt; break;
                case 60:
                    var selected = inventory.Item(int.Parse(values[1]));
                    values[0] = selected.Name; values[1] = selected.Description; values[2] = selected.Attributes;
                    values[3] = selected.Quantities.GetValueOrDefault("player").ToString();
                    values[4] = ItemActions.CanBandage(selected) && selected.Quantities.GetValueOrDefault("player") > 0 ? "1" : "0";
                    string selectedOwner = selected.Quantities.Where(q => q.Value > 0 && inventory.Characters.ContainsKey(q.Key) &&
                        (q.Key == "player" || inventory.Scene?.PresentCharacters.Contains(q.Key) == true))
                        .OrderByDescending(q => q.Key == "player").Select(q => q.Key).FirstOrDefault() ?? "";
                    values[5] = selectedOwner; values[6] = inventory.OwnerName(selectedOwner); break;
                case 61:
                {
                    int materialId = int.Parse(values[1]), targetPage = int.Parse(values[2]);
                    var targets = new List<CharacterStatus>();
                    foreach (var targetStatus in inventory.Statuses)
                    {
                        try { ItemActions.Prepare(inventory, new ItemActionSelection(materialId, "player", "bandage", targetStatus.Id)); targets.Add(targetStatus); }
                        catch (ArgumentException) { }
                    }
                    int targetPages = Math.Max(1, (targets.Count + 9) / 10);
                    targetPage = Math.Clamp(targetPage, 0, targetPages - 1);
                    for (int i = 0; i < 10; i++)
                    {
                        var targetStatus = targets.Skip(targetPage * 10 + i).FirstOrDefault();
                        values[i] = targetStatus == null ? "" : $"[{targetStatus.Id}] {inventory.Characters[targetStatus.Character]}    {targetStatus.Name} / {targetStatus.Severity}";
                    }
                    values[10] = targetPages.ToString(); values[11] = targets.Count.ToString(); break;
                }
                case 62:
                    AiRuntime.PrepareSelectedItem(inventory.Snapshot(), new ItemActionSelection(int.Parse(values[1]), "player", values[2],
                        string.IsNullOrEmpty(values[3]) || values[3] == "0" ? null : int.Parse(values[3]), int.Parse(values[4]))); break;
                case 63:
                    AiRuntime.PrepareSelectedItem(inventory.Snapshot(), new ItemActionSelection(int.Parse(values[1]), values[2], "context")); break;
                case 64:
                    AiRuntime.PrepareContextItems(inventory.Snapshot(), values[1]); break;
                case 85:
                    DrawInventoryGrid(exm, inventory, int.Parse(values[1]), ItemContextSelection.Amounts(values[2]), values[3] == "select"); break;
                case 86:
                    var available = ScenarioActions.MenuFields(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"),
                        int.Parse(values[1]), int.Parse(values[2]), int.Parse(values[3]));
                    for (int i = 0; i < available.Length; i++) values[i] = available[i]; break;
                case 87:
                case 88:
                    if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前对话。");
                    var local = ScenarioActions.CompleteLocal(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"), action == 88 ? "sleep" : "rest");
                    values[0] = local.Snapshot; values[1] = local.Narrative; values[2] = local.Changes; break;
                case 104:
                    var moveOptions = ScenarioActions.MovementTargets(inventory, scenario ?? throw new InvalidOperationException("请先选择剧本。"));
                    values[0] = string.Join("\n", moveOptions.Select(card => $"[{card.Id}] {card.Name}"));
                    values[1] = moveOptions.Count.ToString(); break;
                case 106:
                    if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前对话。");
                    if (scenario == null) throw new InvalidOperationException("请先选择剧本。");
                    ScenarioActions.Ensure(inventory, scenario, "move", int.Parse(values[1]), int.Parse(values[2]), 0);
                    exm.Console.Window.ShowMovementMap(inventory, scenario, int.Parse(values[1]), int.Parse(values[2])); break;
                case 107:
                    if (AiRuntime.Busy || scenario == null) throw new InvalidOperationException("请先结束当前对话并选择剧本。");
                    var routeJson = exm.Console.Window.TakeMapRoute();
                    var routeWorld = NativeSceneWorld.Build(SceneMapDocument.BuildPayload(inventory, scenario.World, DisplayRuntime.Current, scenario));
                    var travelled = new NativeMapTravel(inventory, scenario, routeWorld, int.Parse(values[1]), int.Parse(values[2])).Apply(routeJson);
                    values[0] = travelled.Snapshot; values[1] = travelled.Narrative; values[2] = travelled.TimeCost.ToString(); values[3] = travelled.Changes; break;
                case 108:
                    if (scenario == null) throw new InvalidOperationException("请先选择剧本。");
                    values[0] = $"剩余行动点：{scenario.Parameter("ticks_per_day") - int.Parse(values[1]) - inventory.MovementRemainder:0.###} / {scenario.Parameter("ticks_per_day")}"; break;
                case 110:
                    if (scenario == null) throw new InvalidOperationException("请先选择剧本。");
                    bool actionMeter = values[2] == "action";
                    if (!actionMeter && values[2] != "fatigue") throw new ArgumentException("未知状态框类型。");
                    decimal maximum = actionMeter ? scenario.Parameter("ticks_per_day") : 100;
                    decimal amount = actionMeter ? maximum - int.Parse(values[1]) - inventory.MovementRemainder : FatigueSystem.Points(inventory);
                    exm.Console.PrintBuffer.Append(new ResourceSlotsPart(amount, maximum, actionMeter ? 8 : 10,
                        actionMeter ? ResourceMeterKind.ActionPoints : ResourceMeterKind.Fatigue));
                    values[0] = actionMeter ? $"{amount:0.###} / {maximum:0}" :
                        $"{amount:0.###} / 100 · {FatigueSystem.Label(amount)}" +
                        (FatigueSystem.Penalty(inventory, scenario) is int penalty && penalty > 0 ? $" · 骰点 −{penalty}" : "");
                    break;
                case 105:
                    if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前对话。");
                    if (scenario == null) throw new InvalidOperationException("请先选择剧本。");
                    ScenarioActions.Ensure(inventory, scenario, "move", int.Parse(values[2]), int.Parse(values[3]), 0);
                    var movement = ScenarioActions.ApplyMove(inventory, scenario, int.Parse(values[1]));
                    values[0] = movement.Snapshot; values[1] = movement.Narrative;
                    values[2] = movement.TimeCost.ToString(); values[3] = movement.Changes; break;
                case 89:
                    var people = CharacterOverview.Page(inventory, int.Parse(values[1]));
                    for (int i = 0; i < people.Length; i++) values[i] = people[i]; break;
                case 90:
                    var details = CharacterOverview.Detail(inventory, values[1], int.Parse(values[2]));
                    for (int i = 0; i < details.Length; i++) values[i] = details[i]; break;
                case 109:
                    if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前对话。");
                    if (!inventory.DynamicCharacters.ContainsKey(values[1])) throw new ArgumentException("初始人物层级由剧本资料指定。");
                    inventory.SetCharacterImportance(values[1], values[2]);
                    values[0] = inventory.Snapshot(); break;
                case 91:
                    values[0] = ItemContextSelection.Adjust(inventory, values[1], int.Parse(values[2]), string.IsNullOrEmpty(values[3]) ? 1 : int.Parse(values[3])); break;
                case 92:
                    if (scenario != null) InventoryGrid.CleanLegacy(inventory, scenario.ItemTextMigrations);
                    values[0] = inventory.Snapshot(); break;
                case 96:
                    inventory.EnsureCharacterProfile(values[1], int.Parse(values[2]), int.Parse(values[3]),
                        int.Parse(values[4]), values[5], values[6], values[7] == "1");
                    values[0] = inventory.Snapshot(); break;
                case 94:
                    var useIds = ItemContextSelection.Parse(values[2]);
                    if (useIds.Length != 1) throw new ArgumentException("请选择一种包扎材料。");
                    int useId = useIds[0];
                    if (useId.ToString() != values[2] || !ItemContextSelection.Select(inventory, ItemContextSelection.Parse(values[1])).Contains(useId) ||
                        !ItemActions.CanBandage(inventory.Item(useId)) || inventory.Item(useId).Quantities.GetValueOrDefault("player") < 1)
                        throw new ArgumentException("请先选择自己持有且可用于包扎的物品。");
                    values[0] = useId.ToString(); break;
                case 80:
                    values[0] = inventory.WorldDisplay(); break;
                case 97:
                    exm.Console.Window.ShowSceneMap(inventory, scenario?.World, scenario);
                    break;
                case 38:
                    var lines = inventory.Page(int.Parse(values[1]));
                    for (int i = 0; i < lines.Length; i++) values[i] = lines[i]; break;
                default: throw new ArgumentException("无效的物品操作。");
            }
            exm.VEvaluator.RESULT = 1;
        }
        // Measure against the current canvas and font: changing window size never relies on fixed spaces.
        private static void DrawInventoryGrid(ExpressionMediator exm, InventoryState inventory, int page, Dictionary<int,int> selectedAmounts, bool selecting)
        {
            var selectedIds = selectedAmounts.Keys.ToArray();
            var console = exm.Console;
            console.Window.SyncWindowDrawingWidth();
            float width = Math.Max(120, Config.DrawableWidth - 20);
            int columns = Math.Clamp((int)(width / Math.Max(190, Config.FontSize * 11)), 2, 4);
            float cellWidth = (float)Math.Floor(width / columns);
            var grid = InventoryGrid.Build(inventory, page, selectedIds, columns, 40, selecting);
            var saved = console.StringStyle;
            bool use = console.UseUserStyle, color = console.UseSetColorStyle;
            try
            {
                console.UseUserStyle = console.UseSetColorStyle = true;
                int col = 0;
                foreach (var cell in grid.Cells)
                {
                    console.SetStringStyle(cell.Selected ? (DisplayRuntime.Current == null ? Config.FocusColor : ColorSafety.Selection(DisplayRuntime.Current)) : (DisplayRuntime.Current?.TextColor ?? Config.ForeColor));
                    console.SetStringStyle(cell.Selected ? FontStyle.Bold : FontStyle.Regular);
                    using var font = new Font(Config.FontName, Config.FontSize, cell.Selected ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                    var item = inventory.Item(cell.Id);
                    int count = selectedAmounts.GetValueOrDefault(cell.Id);
                    string suffix = selecting ? "：" + (count > 0 ? count : ItemContextSelection.Available(inventory, item)) : "";
                    string label = selecting ? "[" + cell.Label + "]" + suffix : cell.Label;
                    float Measure(string text) => console.StrMeasure.GetDisplayLength(text, font);
                    while (label.Length > 1 && Measure(label) > cellWidth - Config.FontSize * 2)
                    {
                        string full = selecting ? label[1..label.LastIndexOf(']')] : label;
                        full = full.TrimEnd('…');
                        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(full);
                        if (starts.Length < 2) break;
                        full = full[..starts[^1]] + "…";
                        label = selecting ? "[" + full + "]" + suffix : full;
                    }
                    if (selecting) console.PrintContextItemButton(label, "@item:" + cell.Id, cell.Selected);
                    else console.PrintButton(label, 10000L + cell.Id);
                    float used = Measure(label);
                    console.PrintBuffer.Append(new ConsoleSpacePart(new RectangleF(0, 0, Math.Max(0, cellWidth - used), Config.FontSize)));
                    if (++col == columns) { console.NewLine(); col = 0; }
                }
                if (col > 0 || grid.Cells.Count == 0) console.NewLine();
                if (selecting)
                {
                    console.SetStringStyle(DisplayRuntime.Current?.TextColor ?? Config.ForeColor);
                    console.SetStringStyle(FontStyle.Regular);
                    foreach (int id in selectedIds)
                    {
                        var material = inventory.Item(id);
                        if (ItemActions.CanBandage(material) && material.Quantities.GetValueOrDefault("player") > 0)
                        { console.PrintButton("包扎：" + material.Name, "@use:" + id); console.PrintPlain("    "); }
                    }
                    if (!console.EmptyLine) console.NewLine();
                }
            }
            finally
            {
                console.SetStringStyle(saved.Color); console.SetStringStyle(saved.FontStyle);
                console.UseUserStyle = use; console.UseSetColorStyle = color;
            }
            var values = exm.VEvaluator.RESULTS_ARRAY;
            values[0] = grid.Pages.ToString(); values[1] = grid.Total.ToString();
            values[2] = string.Join("、", selectedAmounts.Select(pair => inventory.Item(pair.Key).Name + " ×" + pair.Value)); values[3] = columns.ToString();
            values[4] = grid.Page.ToString();
        }
        private static void ConfigureScenario(ExpressionMediator exm, int action)
        {
            var values = exm.VEvaluator.RESULTS_ARRAY;
            if (action == 46)
            {
                AiRuntime.ReloadCatalog();
                var entries = AiRuntime.Catalog.Entries;
                int page = (int)Math.Clamp(exm.VEvaluator.RESULT, 0, Math.Max(0, (entries.Count - 1) / 20));
                for (int i = 0; i < 20; i++)
                {
                    int index = page * 20 + i;
                    values[i] = index < entries.Count ? $"[{index + 1}] {entries[index].Name}        作者：{entries[index].Author}        类型：{entries[index].Type}" : "";
                    values[i + 20] = index < entries.Count ? entries[index].Id : "";
                }
                values[40] = string.Join("\n", AiRuntime.Catalog.Errors);
                exm.VEvaluator.RESULT = entries.Count; return;
            }
            if (action == 48)
            {
                exm.VEvaluator.RESULT = AiRuntime.Catalog.Get(values[1]).MatchesSave(values[0]) ? 1 : 0;
                values[0] = ScenarioSavePaths.DisplayCaption(values[0]); return;
            }
            var definition = action == 49
                ? AiRuntime.ActivateScenario(AiRuntime.Catalog.Entries[checked((int)exm.VEvaluator.RESULT)].Id)
                : AiRuntime.ActivateScenario(values[0]);
            var metadata = definition.Metadata();
            for (int i = 0; i < metadata.Length; i++) values[i] = metadata[i];
            exm.VEvaluator.RESULT = 1;
        }
        private static void ConfigureConnection(ExpressionMediator exm, int action)
        {
            if (action == 54)
            {
                var material = exm.VEvaluator.RESULTS_ARRAY;
                string facts = material[0], clock = material[1], rules = material[2];
                exm.VEvaluator.RESULTS = PromptBuilder.Material(facts, clock, rules);
                exm.VEvaluator.RESULT = 1; return;
            }
            if (action is 50 or 51)
            {
                var scenario = AiRuntime.ActiveScenario ?? throw new InvalidOperationException("请先选择剧本。");
                var values = exm.VEvaluator.RESULTS_ARRAY;
                if (action == 50) values[0] = scenario.ContentValue(values[0]);
                else { values[0] = scenario.Id; values[1] = scenario.Name; values[2] = scenario.Author; values[3] = scenario.Type; values[4] = scenario.Version; }
                exm.VEvaluator.RESULT = 1; return;
            }
            if (action is >= 70 and <= 79)
            {
                if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前对话再管理记忆。");
                var values = exm.VEvaluator.RESULTS_ARRAY;
                if (action == 79)
                {
                    var history = AiRuntime.Memory.Read(AiRuntime.ActiveScenario?.Id ?? throw new InvalidOperationException("请先选择剧本。"));
                    var recent = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Reverse(System.Linq.Enumerable.TakeLast(history, 10)));
                    int index = int.Parse(values[1]);
                    if (index == 0)
                    {
                        for (int i = 0; i < 10; i++) values[i + 1] = i < recent.Length ? $"[{i + 1}] {recent[i].Time}  {recent[i].Player}" : "";
                        values[11] = recent.Length.ToString();
                    }
                    else
                    {
                        if (index < 1 || index > recent.Length) throw new ArgumentException("请选择已有对话。");
                        var turn = recent[index - 1]; values[1] = turn.Time; values[2] = turn.Player; values[3] = turn.Reply;
                    }
                    exm.VEvaluator.RESULT = 1; return;
                }
                var snapshot = InventoryState.Read(values[0]);
                var memoryArgs = new string[8]; for (int i = 0; i < memoryArgs.Length; i++) memoryArgs[i] = values[i + 1];
                var result = MemoryCommands.Execute(snapshot, action.ToString(System.Globalization.CultureInfo.InvariantCulture), memoryArgs);
                for (int i = 0; i < result.Length; i++) values[i] = result[i];
                exm.VEvaluator.RESULT = 1; return;
            }
            if (action is 60 or 61 or 62 or 63 or 64 or 80 or 85 or 86 or 87 or 88 or 89 or 90 or 91 or 92 or 94 or 95 or 96 or 97 or 104 or 105 or 106 or 107 or 108 or 109 or 110) { ConfigureInventory(exm, action); return; }
            if (action is 52 or 53 or 55 or 56 or 57) { ConfigureInventory(exm, action); return; }
            if (action is >= 46 and <= 49)
            {
                ConfigureScenario(exm, action); return;
            }
            if (action is >= 30 and <= 45)
            {
                ConfigureInventory(exm, action);
                return;
            }
            if (action is 20 or 21 or 22)
            {
                if (AiRuntime.HistoryBusy) throw new InvalidOperationException("请等待当前对话完成后再保存或恢复 AI 历史。");
                var values = exm.VEvaluator.RESULTS_ARRAY;
                if (action == 20) exm.VEvaluator.RESULTS = AiRuntime.Memory.Snapshot(exm.VEvaluator.RESULTS);
                else if (action == 21) AiRuntime.Memory.Restore(values[1], exm.VEvaluator.RESULTS);
                else AiRuntime.Memory.Append(values[1], new ConversationTurn(values[2], values[3], values[4]));
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (action == 18)
            {
                var connection = AiRuntime.Settings.Load();
                if (connection.Mode != "api") throw new InvalidOperationException("消耗统计仅适用于网络 API。");
                exm.Console.Window.ShowUsageChart(AiRuntime.Usage.SevenDays(connection));
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (action == 19) { exm.VEvaluator.RESULT = exm.Console.Window.UsageChartActive ? 0 : 1; return; }
            if (action is 14 or 17) { AiRuntime.EnsureAccount(action == 17); exm.VEvaluator.RESULT = 1; return; }
            if (action == 15) { exm.VEvaluator.RESULT = AiRuntime.PollAccount(); return; }
            if (action == 16)
            {
                var lines = AiRuntime.AccountLines();
                for (int i = 0; i < lines.Length; i++) exm.VEvaluator.RESULTS_ARRAY[i] = lines[i];
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (action == 9) { exm.Console.Window.BeginSecretInput(); return; }
            if (action == 10) { exm.Console.Window.EndSecretInput(); return; }
            if (action == 5 && exm.Console.Window.HasSecretInputSession)
            {
                string protectedKey = exm.Console.Window.TakeSecretInput();
                if (protectedKey.Length > 0)
                    AiRuntime.Settings.SaveConnection(AiRuntime.Settings.Load() with { ProtectedKey = protectedKey });
                exm.VEvaluator.RESULTS = "";
                exm.VEvaluator.RESULT = protectedKey.Length == 0 ? 0 : 1;
                return;
            }
            if (action == 8) { AiRuntime.StartModelList(); exm.VEvaluator.RESULT = 0; return; }
            if (action == 11)
            {
                exm.VEvaluator.RESULT = AiRuntime.PollModelList(out string error);
                exm.VEvaluator.RESULTS = error;
                return;
            }
            var settings = AiRuntime.Settings.Load();
            string input = exm.VEvaluator.RESULTS;
            if (action is 12 or 13)
            {
                if (AiRuntime.ModelsConnection == null || settings.Mode != AiRuntime.ModelsConnection.Mode ||
                    settings.BaseUrl != AiRuntime.ModelsConnection.BaseUrl || settings.ProtectedKey != AiRuntime.ModelsConnection.ProtectedKey)
                    throw new InvalidOperationException("连接设置已变化，请重新读取模型列表。");
                if (action == 13)
                {
                    int page = (int)Math.Clamp(exm.VEvaluator.RESULT, 0, Math.Max(0, (AiRuntime.Models.Length - 1) / 20));
                    for (int i = page * 20; i < Math.Min(AiRuntime.Models.Length, (page + 1) * 20); i++)
                        exm.Console.PrintSingleLine($"[{i + 1}] {AiRuntime.Models[i].Replace('[', '（').Replace(']', '）')}" + (AiRuntime.Models[i] == settings.Model ? "（当前）" : ""));
                    exm.VEvaluator.RESULT = AiRuntime.Models.Length;
                    return;
                }
                long index = exm.VEvaluator.RESULT - 1;
                if (index < 0 || index >= AiRuntime.Models.Length) throw new ArgumentException("请选择列表中的模型编号。");
                AiRuntime.Settings.SaveConnection(settings with { Model = AiRuntime.Models[index] });
                exm.VEvaluator.RESULT = 1;
                exm.VEvaluator.RESULTS = "";
                return;
            }
            if (action == 0)
            {
                exm.VEvaluator.RESULTS = settings.Model.Length == 0 ? "尚未设置" : settings.Model;
                exm.VEvaluator.RESULTS_ARRAY[1] = settings.ModeName;
                exm.VEvaluator.RESULTS_ARRAY[2] = settings.BaseUrl;
                exm.VEvaluator.RESULTS_ARRAY[3] = settings.ProtectedKey.Length == 0 ? "未填写" : settings.GetKey().Length < 8 ? "内容不完整，请重新填写" : "已填写（隐藏）";
                exm.VEvaluator.RESULTS_ARRAY[4] = settings.TimeoutSeconds.ToString();
                exm.VEvaluator.RESULT = settings.Mode == "local" ? 0 : 1;
                return;
            }
            if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前生成，再修改连接设置。");
            if (action is 1 or 2) settings = AiRuntime.Settings.LoadConnection(action == 1 ? "local" : "api");
            else if (action == 7) settings = settings with { ProtectedKey = "" };
            else
            {
                input = input.Trim();
                if (input.Length == 0) { exm.VEvaluator.RESULT = 0; return; }
                switch (action)
                {
                    case 3:
                        settings = settings.WithServiceAddress(input);
                        break;
                    case 4: settings = settings with { Model = input }; break;
                    case 5: settings = settings with { ProtectedKey = SecretProtection.Protect(input) }; break;
                    case 6:
                        if (!int.TryParse(input, out int seconds)) throw new ArgumentException("请输入 10～600 的整数秒数。");
                        settings = settings with { TimeoutSeconds = seconds };
                        break;
                    default: throw new ArgumentException("无效的模型连接操作。");
                }
            }
            AiRuntime.Settings.SaveConnection(settings);
            exm.VEvaluator.RESULTS = "";
            exm.VEvaluator.RESULT = 1;
        }
    }
}
