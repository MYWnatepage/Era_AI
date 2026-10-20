using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class AccountTests
{
    public static async Task Run()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new Exception("账户统计测试失败：" + message);
            Console.WriteLine("通过：" + message);
        }
        static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
            { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        var settings = new AiSettings { Mode = "api", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-flash", ProtectedKey = SecretProtection.Protect("fake-balance-test-key") };
        int calls = 0;
        var balanceClient = new AccountBalanceClient(new HttpClient(new FakeHandler((request, _) =>
        {
            calls++;
            Check(request.Method == HttpMethod.Get && request.RequestUri!.ToString() == "https://api.deepseek.com/user/balance", "余额使用独立 GET 接口，不调用模型");
            Check(request.Headers.Authorization?.Parameter == "fake-balance-test-key", "余额使用当前连接的密钥");
            return Task.FromResult(Json("{\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"12.345\"},{\"currency\":\"USD\",\"total_balance\":\"0.00\"}]}"));
        })));
        var balance = await balanceClient.ReadAsync(settings, default);
        Check(balance.Success && balance.Text == "12.345 CNY / 0.00 USD", "解析多币种真实余额并保留小额精度");
        foreach (string url in new[] { "https://api.openai.com/v1", "https://api.deepseek.com.attacker.invalid", "http://api.deepseek.com", "https://api.deepseek.com/unexpected" })
            Check(!(await balanceClient.ReadAsync(settings with { BaseUrl = url }, default)).Success, "不向未适配的主机或路径发送余额请求");
        Check(calls == 1, "不支持的平台不会产生余额网络请求");
        Check(!(await balanceClient.ReadAsync(settings with { ProtectedKey = "" }, default)).Success && calls == 1, "无密钥时不请求余额");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests })
        {
            var errorClient = new AccountBalanceClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("private-server-body", status)))));
            var result = await errorClient.ReadAsync(settings, default);
            Check(!result.Success && !result.Text.Contains("private-server-body"), "余额错误不暴露服务返回正文");
        }
        var malformed = new AccountBalanceClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"balance_infos\":[null,3,{}]}")))));
        Check(!(await malformed.ReadAsync(settings, default)).Success, "余额格式异常不会显示伪造的零余额");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var waiting = new AccountBalanceClient(new HttpClient(new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        })));
        Check((await waiting.ReadAsync(settings, cancel.Token)).Cancelled, "余额查询可以用取消键终止");

        var dir = Path.Combine(Path.GetTempPath(), "EraAI-account-tests-" + Guid.NewGuid().ToString("N"));
        DateTimeOffset now = new(2026, 10, 19, 15, 59, 0, TimeSpan.Zero);
        var store = new AccountUsageStore(dir, () => now);
        using var doc = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":200,\"prompt_cache_hit_tokens\":100}} ");
        var usage = TokenUsage.Read(doc.RootElement)!;
        Check(usage.Input == 1000 && usage.Output == 200 && usage.CacheHit == 100, "读取服务返回的实际输入、输出和缓存用量");
        store.Record(settings, usage, "deepseek-flash");
        var today = store.Read(settings);
        Check(today.Input == 1000 && today.Output == 200 && today.Requests == 1, "按当前连接累计今日用量");
        Check(today.CostMin == .001702m && today.CostMax == .003404m, "按缓存命中和高峰/空闲价格计算花费区间");
        Check(new AccountUsageStore(dir, () => now).Read(settings).Input == 1000, "重启后仍能读取今日统计");
        Check(store.Read(settings with { ProtectedKey = SecretProtection.Protect("different-account-key") }).Requests == 0, "更换密钥不会混用其他连接的统计");
        Check(store.Read(settings with { BaseUrl = "https://api.deepseek.com" }).Input == 1000, "DeepSeek 根地址与 v1 地址共享同一连接统计");
        Check(!File.ReadAllText(Path.Combine(dir, "api-usage.json")).Contains("fake-balance-test-key"), "用量文件不保存密钥明文");
        now = now.AddMinutes(2);
        Check(store.Read(settings).Requests == 0, "北京时间跨日后今日统计自动归零");
        store.Record(settings, null!, "deepseek-flash");
        Check(AccountUsageStore.CostText(store.Read(settings)).Contains("无法完整估算"), "未返回用量不会伪装成零消费");
        Check(AccountUsageStore.Estimate(settings, "unknown-model", usage) == null, "未知模型不会套用错误价格");
        Check(AccountUsageStore.Estimate(settings with { BaseUrl = "https://other.example/v1" }, "deepseek-flash", usage) == null, "第三方同名模型不会套用官方价格");

        var integrationDir = Path.Combine(dir, "integration");
        var integrationStore = new AccountUsageStore(integrationDir);
        var memory = new ConversationStore(Path.Combine(dir, "memory"));
        var chat = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"model\":\"deepseek-flash\",\"choices\":[{\"message\":{\"content\":\"连接成功\"}}],\"usage\":{\"prompt_tokens\":60,\"completion_tokens\":24}}")))), memory, integrationStore);
        await chat.SendAsync(settings, new AiRequest("测试", "测试", "connection_test", "", 64, 0, ""), default);
        Check(integrationStore.Read(settings).Input == 60 && integrationStore.Read(settings).Output == 24 && memory.Read("connection_test").Count == 0, "连接测试计入用量且不会写入剧情记忆");
        var emptyChat = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":4}}")))), memory, integrationStore);
        Check(!(await emptyChat.SendAsync(settings, new AiRequest("测试", "测试", "connection_test", "", 64, 0, ""), default)).Success && integrationStore.Read(settings).Input == 70, "无最终回复但已返回用量的请求也计入统计");
        var local = settings with { Mode = "local" };
        store.Record(local, usage, local.Model);
        Check(store.Read(local).Requests == 0, "本地模型不会增加网络 API 用量");
    }
}
