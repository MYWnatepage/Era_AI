using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ReviewRegressionTests
{
    public static async Task Run()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new Exception("检查回归失败：" + message);
            Console.WriteLine("通过：" + message);
        }
        static HttpClient Client(string json) => new(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") })));
        var settings = new AiSettings { Mode = "api", BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash", ProtectedKey = SecretProtection.Protect("fake-review-test-key") };
        var dir = Path.Combine(Path.GetTempPath(), "EraAI-review-" + Guid.NewGuid().ToString("N"));
        var memory = new ConversationStore(Path.Combine(dir, "memory"));
        var request = new AiRequest("测试", "测试", "connection_test", "", 64, 0, "");
        bool expectedProbe = true;
        var probeHttp = new HttpClient(new FakeHandler(async (http, token) =>
        {
            using var data = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(token));
            bool hasSwitch = data.RootElement.TryGetProperty("thinking", out var thinking);
            Check(expectedProbe ? hasSwitch && thinking.GetProperty("type").GetString() == "disabled" : !hasSwitch,
                "仅 DeepSeek 连接测试关闭思考，普通对话保持原设置");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"正常回复\"}}]}") };
        }));
        var probe = new AiClient(probeHttp, memory);
        Check((await probe.SendAsync(settings, request, default)).Success, "短回复连接测试正常处理");
        expectedProbe = false;
        Check((await probe.SendAsync(settings, request with { Scenario = "rain_room" }, default)).Success, "普通对话不强制改变思考参数");
        Check((await probe.SendAsync(settings with { BaseUrl = "https://custom.invalid/v1" }, request, default)).Success, "自定义平台不携带 DeepSeek 专属参数");
        foreach (string body in new[] { "[]", "null", "{\"choices\":{}}", "{\"choices\":[null]}", "{\"choices\":[{\"message\":[]}]}" })
        {
            var result = await new AiClient(Client(body), memory).SendAsync(settings, request, default);
            Check(!result.Success && result.Text.Contains("接口兼容性"), "异常聊天响应形状返回中文提示，不抛异常");
        }
        foreach (string body in new[] { "[]", "null", "{\"data\":{}}" })
            Check(!(await new ModelCatalogClient(Client(body)).ReadAsync(settings, default)).Success, "异常模型列表形状不会导致未处理错误");
        foreach (string body in new[] { "[]", "null", "{\"balance_infos\":{}}" })
            Check(!(await new AccountBalanceClient(Client(body)).ReadAsync(settings, default)).Success, "异常余额响应形状不会导致未处理错误");

        var concurrentDir = Path.Combine(dir, "concurrent");
        var writers = Enumerable.Range(0, 4).Select(_ => new AccountUsageStore(concurrentDir)).ToArray();
        await Task.WhenAll(writers.Select(store => Task.Run(() =>
        {
            for (int i = 0; i < 20; i++) store.Record(settings, new TokenUsage(10, 5, 0), settings.Model);
        })));
        var day = new AccountUsageStore(concurrentDir).Read(settings);
        Check(day.Requests == 80 && day.Input == 800 && day.Output == 400, "多个用量记录实例同时写入不会丢失或覆盖累计值");

        var brokenDir = Path.Combine(dir, "broken");
        Directory.CreateDirectory(brokenDir);
        string file = Path.Combine(brokenDir, "api-usage.json");
        var usage = new AccountUsageStore(brokenDir);
        foreach (string data in new[] { "null", "[null]", "[{\"Input\":-1}]" })
        {
            File.WriteAllText(file, data);
            var result = await new AiClient(Client("{\"choices\":[{\"message\":{\"content\":\"正常回复\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}"), memory, usage).SendAsync(settings, request, default);
            Check(result.Success && result.Text == "正常回复" && result.Warning.Contains("用量记录保存失败"), "用量文件格式损坏仍保留已生成回复并提示统计失败");
            Check(File.ReadAllText(file) == data, "不会将异常用量文件清空后覆盖");
        }
    }
}
