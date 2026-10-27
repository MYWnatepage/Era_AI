using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeAI;

internal static class AiRuntime
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    internal static readonly AiSettingsStore Settings = new(System.IO.Path.Combine(Program.ExeDir, "配置"));
    internal static readonly ConversationStore Memory = new(System.IO.Path.Combine(Program.ExeDir, "数据", "记忆"));
    internal static readonly AccountUsageStore Usage = new(System.IO.Path.Combine(Program.ExeDir, "数据", "账户"));
    internal static readonly GenerationSettingsStore Generation = new(System.IO.Path.Combine(Program.ExeDir, "配置"));
    private static readonly ModelLimitsStore Limits = new(System.IO.Path.Combine(Program.ExeDir, "配置"));
    private static readonly AiClient Client = new(Http, Memory, Usage);
    private static Task<BalanceResult> pendingBalance;
    private static CancellationTokenSource balanceCancellation;
    private static string balanceAccount = "";
    private static string balanceText = "尚未查询";
    private static DateTimeOffset balanceTime;
    private static CancellationTokenSource pendingCancellation;
    private static Task<AiResult> pending;
    internal static (int Roll, string Summary)? D20Presentation { get; private set; }
    private static Task<ModelCatalogResult> pendingModels;
    public static string[] Models { get; private set; } = [];
    public static AiSettings ModelsConnection { get; private set; }
    private static AiResult last = new(false, "尚未发送对话。");
    public static bool Busy => (pending != null && !pending.IsCompleted) || (pendingModels != null && !pendingModels.IsCompleted) || (pendingBalance != null && !pendingBalance.IsCompleted);
    internal static bool HistoryBusy => pending != null && !pending.IsCompleted;
    public static string LastText => last.Text;
    public static string LastWarning => last.Warning;
    public static string LastInventory => last.InventorySnapshot;
    public static string LastInventoryChanges => last.InventoryChanges;
    public static string LastSuggestions => last.Suggestions;
    public static bool LastRequiresApproval => last.RequiresApproval;
    public static string LastRationale => last.Rationale;
    private static InventoryExchange preparedInventory;
    internal static ScenarioCatalog Catalog { get; private set; } = ScenarioCatalog.Load(System.IO.Path.Combine(Program.ExeDir, "ERB", "script"));
    internal static ScenarioDefinition ActiveScenario { get; private set; }
    internal static void ReloadCatalog() => Catalog = ScenarioCatalog.Load(System.IO.Path.Combine(Program.ExeDir, "ERB", "script"));
    internal static ScenarioDefinition ActivateScenario(string id)
    {
        var entry = Catalog.Get(id);
        if (ActiveScenario?.Id != id)
        {
            if (HistoryBusy) throw new InvalidOperationException("请等待当前对话完成后再切换剧本。");
            preparedInventory = null;
        }
        MinorShift.Emuera.Runtime.Utils.ScenarioSavePaths.Select(entry.Id, entry.MatchesSave);
        ActiveScenario = entry;
        return entry;
    }
    internal static void PrepareInventory(string snapshot, ScenarioActionOutcome action = null)
    {
        if (Busy) throw new InvalidOperationException("请等待当前对话完成。");
        preparedInventory = null;
        if (ActiveScenario == null) throw new InvalidOperationException("请先选择已登记的剧本。");
        var state = InventoryState.Read(snapshot);
        state.BindOwners(ActiveScenario.Owners);
        state.BindCharacters(ActiveScenario.Characters);
        state.BindWorld(ActiveScenario);
        if (action != null) action = action with { EndScene = state.ResolveActionScene(action.EndScene) };
        preparedInventory = new(state.Snapshot(), Guid.NewGuid().ToString("N"), action, ActiveScenario.Id, ActiveScenario.InventoryInstruction,
            SelectionRules: ActiveScenario.ContextSelection, Scenario: ActiveScenario);
    }
    internal static void PrepareSelectedItem(string snapshot, ItemActionSelection selection)
    {
        PrepareInventory(snapshot);
        ItemActions.Prepare(InventoryState.Read(preparedInventory.Snapshot), selection, scenario: ActiveScenario);
        preparedInventory = preparedInventory with { SelectedItem = selection };
    }
    internal static void PrepareContextItems(string snapshot, IEnumerable<int> selectedIds)
    {
        if (Busy) throw new InvalidOperationException("请等待当前对话完成。");
        var previous = preparedInventory;
        preparedInventory = null;
        if (ActiveScenario == null) throw new InvalidOperationException("请先选择已登记的剧本。");
        var state = InventoryState.Read(snapshot);
        state.BindOwners(ActiveScenario.Owners);
        state.BindCharacters(ActiveScenario.Characters);
        state.BindWorld(ActiveScenario);
        string current = state.Snapshot();
        int[] selected = ItemContextSelection.Select(state, selectedIds);
        if (previous != null && ItemContextSelection.Matches(previous, current, ActiveScenario.Id))
            preparedInventory = ItemContextSelection.Attach(previous, selected);
        else
        {
            PrepareInventory(current);
            preparedInventory = ItemContextSelection.Attach(preparedInventory, selected);
        }
    }
    internal static void PrepareContextItems(string snapshot, string csv)
    {
        var amounts = ItemContextSelection.ValidateAmounts(InventoryState.Read(snapshot), csv);
        PrepareContextItems(snapshot, amounts.Keys);
        preparedInventory = preparedInventory with { SelectedAmounts = amounts };
    }
    public static void Start(AiRequest request)
    {
        if (Busy) throw new InvalidOperationException("已有对话正在生成，请等待或取消。");
        if (preparedInventory != null)
        {
            var prepared = preparedInventory;
            preparedInventory = null;
            if (request.Scenario != prepared.ScenarioId) throw new InvalidOperationException("准备的物品状态不属于当前请求的剧本。");
            request = request with { Inventory = prepared with { GameTime = request.GameTime } };
        }
        preparedInventory = null;
        var connection = Settings.Load();
        if (request.Scenario != "connection_test") request = request with { Parameters = Generation.Load(connection), Limits = GetModelLimits(connection) };
        pendingCancellation?.Dispose();
        pendingCancellation = new CancellationTokenSource();
        var token = pendingCancellation.Token;
        D20Presentation = request.Inventory?.Action is { Sides: 20 } action ? (action.RawRoll > 0 ? action.RawRoll : action.Roll, action.Summary) :
            request.Inventory?.SelectedItem is { Mode: "bandage", Roll: > 0 } selected
                ? (selected.Roll, ItemActions.Prepare(InventoryState.Read(request.Inventory.Snapshot), selected, request.GameTime, request.Inventory.Scenario).RollSummary + " / 包扎效果判定") : null;
        pending = Task.Run(async () =>
        {
            try { return await Client.SendAsync(connection, request, token).ConfigureAwait(false); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return new AiResult(false, e.Message); }
            catch { return new AiResult(false, "模型调用失败，请检查模型设置和记忆文件。"); }
        });
    }
    public static int Poll(out string text)
    {
        if (pending != null)
        {
            if (!pending.IsCompleted) { text = ""; return 0; }
            last = pending.GetAwaiter().GetResult();
            pending = null;
            balanceTime = default;
        }
        text = last.Text;
        return last.Success ? 1 : -1;
    }
    public static void Cancel() { pendingCancellation?.Cancel(); balanceCancellation?.Cancel(); }

    public static void EnsureAccount(bool force = false)
    {
        var settings = Settings.Load();
        if (settings.Mode != "api") return;
        string id = ApiAccount.Id(settings);
        if (pendingBalance != null && balanceAccount == id) return;
        if (!force && balanceAccount == id && DateTimeOffset.UtcNow - balanceTime < TimeSpan.FromSeconds(60)) return;
        balanceCancellation?.Cancel();
        balanceCancellation?.Dispose();
        balanceCancellation = new();
        var token = balanceCancellation.Token;
        balanceAccount = id;
        balanceText = "正在查询…";
        balanceTime = default;
        pendingBalance = Task.Run(async () =>
        {
            try { return await new AccountBalanceClient(Http).ReadAsync(settings, token).ConfigureAwait(false); }
            catch { return new BalanceResult(false, "查询失败，请检查连接设置后刷新"); }
        });
    }

    // 0: waiting, 2: just completed (redraw once), 1: idle, 3: cancelled.
    public static int PollAccount()
    {
        if (pendingBalance == null) return 1;
        if (!pendingBalance.IsCompleted) return 0;
        var result = pendingBalance.GetAwaiter().GetResult();
        pendingBalance = null;
        balanceText = result.Text;
        balanceTime = DateTimeOffset.UtcNow;
        return result.Cancelled ? 3 : 2;
    }

    public static string[] AccountLines()
    {
        var settings = Settings.Load();
        if (settings.Mode != "api") return ["", "", ""];
        string balance = balanceAccount == ApiAccount.Id(settings) ? balanceText : "尚未查询";
        if (balanceAccount == ApiAccount.Id(settings) && balanceTime != default)
            balance += "（" + balanceTime.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss") + " 更新）";
        try
        {
            var day = Usage.Read(settings);
            string tokens = $"输入 {day.Input:N0} / 输出 {day.Output:N0} Token";
            if (day.MissingUsage > 0) tokens += $"（{day.MissingUsage} 次请求未返回用量）";
            return [balance, AccountUsageStore.CostText(day), tokens];
        }
        catch { return [balance, "统计读取失败，请检查数据目录", "统计读取失败"]; }
    }
    internal static ModelLimits GetModelLimits(AiSettings connection)
    {
        try { return Limits.Get(connection); }
        catch { return new(connection.Model); }
    }
    internal static string LimitsStatus(AiSettings connection)
    {
        try { return Limits.Status(connection); }
        catch { return "容量缓存读取失败，可重新刷新"; }
    }
    public static void StartModelList()
    {
        if (Busy) throw new InvalidOperationException("已有请求正在进行，请先等待或取消。");
        var settings = Settings.Load();
        if (settings.Mode != "api") throw new InvalidOperationException("本地大模型（未完成），请先切换到网络 API。");
        settings.ValidateConnection();
        pendingCancellation?.Dispose();
        pendingCancellation = new CancellationTokenSource();
        var token = pendingCancellation.Token;
        Models = [];
        ModelsConnection = settings;
        pendingModels = Task.Run(async () =>
        {
            try { return await new ModelCatalogClient(Http).ReadAsync(settings, token).ConfigureAwait(false); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return new ModelCatalogResult(false, [], e.Message); }
            catch { return new ModelCatalogResult(false, [], "读取模型列表失败，请检查连接设置。"); }
        });
    }
    public static int PollModelList(out string error)
    {
        error = "";
        if (pendingModels == null) { error = "请先读取模型列表。"; return -1; }
        if (!pendingModels.IsCompleted) return 0;
        var result = pendingModels.GetAwaiter().GetResult();
        pendingModels = null;
        Models = result.Models;
        if (result.Success && ModelsConnection != null)
        {
            try { Limits.Save(ModelsConnection, result.Limits ?? []); }
            catch { /* A cache write failure must not prevent model selection. */ }
        }
        error = result.Error;
        return result.Success ? 1 : -1;
    }
    public static void Configure()
    {
        if (Busy) { MessageBox.Show("请等待当前对话完成后再修改设置。", "AI 设置"); return; }
        using var form = new AiSettingsForm(Settings, Client);
        form.ShowDialog(Form.ActiveForm);
    }
}
