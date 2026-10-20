using System;
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
    private static Task<ModelCatalogResult> pendingModels;
    public static string[] Models { get; private set; } = [];
    public static AiSettings ModelsConnection { get; private set; }
    private static AiResult last = new(false, "尚未发送对话。");
    public static bool Busy => (pending != null && !pending.IsCompleted) || (pendingModels != null && !pendingModels.IsCompleted) || (pendingBalance != null && !pendingBalance.IsCompleted);
    internal static bool HistoryBusy => pending != null && !pending.IsCompleted;
    public static string LastText => last.Text;
    public static string LastWarning => last.Warning;
    public static void Start(AiRequest request)
    {
        if (Busy) throw new InvalidOperationException("已有对话正在生成，请等待或取消。");
        var connection = Settings.Load();
        if (request.Scenario != "connection_test") request = request with { Parameters = Generation.Load(connection), Limits = GetModelLimits(connection) };
        pendingCancellation?.Dispose();
        pendingCancellation = new CancellationTokenSource();
        var token = pendingCancellation.Token;
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
