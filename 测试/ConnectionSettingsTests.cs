using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ConnectionSettingsTests
{
    public static void Run()
    {
        static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("模型配置测试失败：" + label);
            Console.WriteLine("通过：" + label);
        }
        string folder = Path.Combine(Path.GetTempPath(), "EraAI-connections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "ai-settings.json");
        var local = new AiSettings { Model = "local-test", BaseUrl = "http://127.0.0.1:1234/v1" };
        File.WriteAllText(path, JsonSerializer.Serialize(local));
        var store = new AiSettingsStore(folder);
        Check(store.Load() == local && store.LoadConnection("local") == local, "兼容旧版单连接配置");
        var api = store.LoadConnection("api");
        store.SaveConnection(api);
        Check(store.Load().Mode == "api" && store.LoadConnection("local") == local, "切换接入方式保留本地配置");
        try { store.Load().Validate(); throw new Exception("未填完整的 API 不应发出请求"); }
        catch (ArgumentException) { Check(true, "未完成配置允许保存，发请求前仍会检查"); }
        api = api with { BaseUrl = "https://example.invalid/v1", Model = "api-test", ProtectedKey = SecretProtection.Protect("connection-secret"), TimeoutSeconds = 90 };
        store.Save(api);
        store.SaveConnection(store.LoadConnection("local"));
        Check(store.Load() == local && store.LoadConnection("api") == api, "本地和网络模型、地址、密钥与超时分别记住");
        Check(store.LoadConnection("api").GetKey() == "connection-secret" && !File.ReadAllText(path).Contains("connection-secret"), "独立配置中的密钥继续加密保存");
        Check(api.WithServiceAddress("https://example.invalid/v1/").ProtectedKey == api.ProtectedKey, "重复选择同一服务保留密钥");
        var switched = api.WithServiceAddress("https://api.deepseek.com");
        Check(switched.ProtectedKey.Length == 0 && switched.Model.Length == 0, "更换平台清除旧密钥和旧模型");
        Check(api.WithServiceAddress("https://example.invalid/v2").ProtectedKey == api.ProtectedKey, "同平台更换接口路径不误清密钥");
        var original = File.ReadAllText(path);
        try { store.SaveConnection(local with { TimeoutSeconds = 1 }); throw new Exception("无效超时不应保存"); }
        catch (ArgumentException) { Check(File.ReadAllText(path) == original, "无效修改不覆盖已有连接"); }
    }
}
