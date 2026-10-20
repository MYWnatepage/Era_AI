using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

int startupIndex = Array.IndexOf(args, "--startup-only");
if (startupIndex >= 0 && startupIndex + 1 < args.Length)
{
    var runner = System.Reflection.Assembly.LoadFrom(args[startupIndex + 1]);
    var entry = runner.EntryPoint ?? throw new Exception("没有找到程序入口。");
    if (!entry.IsDefined(typeof(STAThreadAttribute), false)) throw new Exception("游戏入口缺少 STA 标记，剪贴板操作会失败。");
    Console.WriteLine("通过：游戏真实入口具有 STA 标记，剪贴板使用正确的线程模式。");
    return;
}

var directory = Path.Combine(Path.GetTempPath(), "EraAI-tests-" + Guid.NewGuid().ToString("N"));
DisplaySafetyTests.Run();
GameColorEditorTests.Run();
if (args.Contains("--display-only")) return;
ConnectionSettingsTests.Run();
SecretInputTests.Run();
await ModelCatalogTests.Run();
await AccountTests.Run();
await ReviewRegressionTests.Run();
UsageChartTests.Run();
await GenerationTests.Run();
HistorySyncTests.Run();
Directory.CreateDirectory(directory);
var memory = new ConversationStore(Path.Combine(directory, "memory"));
var settingsStore = new AiSettingsStore(Path.Combine(directory, "config"));
var secret = "test-secret-中文";
var settings = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid/v1", Model = "test-model", ProtectedKey = SecretProtection.Protect(secret), TimeoutSeconds = 10 };
settingsStore.Save(settings);
Assert(settingsStore.Load().GetKey() == secret, "密钥加密往返");
Assert(!File.ReadAllText(Path.Combine(directory, "config", "ai-settings.json")).Contains(secret), "磁盘设置不含明文密钥");
var weird = "他说\"你好\"，路径 C:\\故事\\雨夜，换行\n和 | 符号。";
var handler = new FakeHandler(async (http, ct) =>
{
    Assert(http.RequestUri!.ToString() == "https://example.invalid/v1/chat/completions", "服务地址拼接");
    Assert(http.Headers.Authorization!.Parameter == secret, "请求认证");
    var raw = await http.Content!.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(raw);
    Assert(doc.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() == weird, "引号、反斜杠、换行与分隔符正确序列化");
    Assert(doc.RootElement.GetProperty("max_tokens").GetInt32() == 512, "回复上限传递");
    return JsonResponse("<think>内部推理</think>欢迎回来。");
});
var client = new AiClient(new HttpClient(handler), memory);
var request = new AiRequest(weird, weird, "rain_room", "第1天", 512, 1024, "温柔");
var result = await client.SendAsync(settings, request, default);
Assert(result.Success && result.Text == "欢迎回来。", "过滤思考文本并返回最终回复");
Assert(memory.Read("rain_room").Count == 1 && memory.Read("rain_room")[0].Player == weird, "成功对话持久化");
Assert(memory.Context("rain_room", 0).Count == 0, "记忆容量为零时不传历史");
Assert(memory.Context("rain_room", 1024).Count == 1, "记忆预算正常读取");
var count = memory.Read("rain_room").Count;
foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
{
    var failing = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(secret) }))), memory);
    var failure = await failing.SendAsync(settings, request, default);
    Assert(!failure.Success && !failure.Text.Contains(secret), $"HTTP {(int)status} 错误不泄露服务响应或密钥");
}
Assert(memory.Read("rain_room").Count == count, "失败请求不污染记忆");
var badJson = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("bad-json") }))), memory);
Assert(!(await badJson.SendAsync(settings, request, default)).Success, "无效回复格式");
var empty = new AiClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse("")))), memory);
Assert(!(await empty.SendAsync(settings, request, default)).Success, "空回复不作为成功对话");
var slow = new AiClient(new HttpClient(new FakeHandler(async (_, ct) => { await Task.Delay(30000, ct); return JsonResponse("不会返回"); })), memory);
using (var cancel = new CancellationTokenSource(50))
{
    var canceled = await slow.SendAsync(settings, request, cancel.Token);
    Assert(!canceled.Success && canceled.Text.Contains("取消"), "可取消正在等待的请求");
}
var timeoutResult = await slow.SendAsync(settings, request, default);
Assert(!timeoutResult.Success && timeoutResult.Text.Contains("超时"), "请求超时后恢复");
var local = settings with { Mode = "local", ProtectedKey = "" };
local.Validate();
Assert(true, "本地模式允许无密钥");
try { memory.Read("../escape"); throw new Exception("路径限制失效"); }
catch (ArgumentException) { Assert(true, "剧本记忆目录限制"); }
memory.Clear("rain_room");
Assert(memory.Read("rain_room").Count == 0, "清空长期记忆");
Console.WriteLine("全部原生 AI 核心测试通过。测试目录：" + directory);

static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json"),
};
static void Assert(bool condition, string description)
{
    if (!condition) throw new Exception("失败：" + description);
    Console.WriteLine("通过：" + description);
}
sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
