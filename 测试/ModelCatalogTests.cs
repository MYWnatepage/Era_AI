using System.Net;
using System.Text;
using MinorShift.Emuera.NativeAI;

internal static class ModelCatalogTests
{
    public static async Task Run()
    {
        static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("模型列表测试失败：" + label);
            Console.WriteLine("通过：" + label);
        }
        var settings = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", ProtectedKey = SecretProtection.Protect("catalog-test-key") };
        var client = new ModelCatalogClient(new HttpClient(new FakeHandler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.ToString() == "https://example.invalid/v1/models", "读取服务真实模型列表接口");
            Check(request.Headers.Authorization?.Parameter == "catalog-test-key", "模型列表使用当前密钥认证");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"model-b\"},{\"id\":\"model-a\"},{\"id\":\"model-a\"},{\"id\":\"\"},{\"id\":\"bad\\nname\"}]}", Encoding.UTF8, "application/json") });
        })));
        var result = await client.ReadAsync(settings, default);
        Check(result.Success && result.Models.SequenceEqual(new[] { "model-a", "model-b" }), "没有预填模型名称也能读取，列表去重并排除无效值");
        var failed = new ModelCatalogClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("secret-server-body") }))));
        var error = await failed.ReadAsync(settings, default);
        Check(!error.Success && error.Error.Contains("模型列表接口") && !error.Error.Contains("secret-server-body"), "不支持读取的接口给出提示且不泄露响应正文");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var waiting = new ModelCatalogClient(new HttpClient(new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        Check((await waiting.ReadAsync(settings, cancellation.Token)).Error == "本次请求已取消。", "读取模型列表可以取消");
    }
}
