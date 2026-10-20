using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class HistorySyncTests
{
    internal static void Run()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            Console.WriteLine("通过：" + message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "EraAI-history-sync-" + Guid.NewGuid().ToString("N"));
        var store = new ConversationStore(directory);
        store.Append("rain_room", new("day1", "旧输入", "旧回应"));
        string saved = store.Snapshot("rain_room");
        store.Append("rain_room", new("day2", "未来输入", "未来回应"));
        store.Restore("rain_room", saved);
        Check(store.Read("rain_room").Count == 1 && store.Read("rain_room")[0].Player == "旧输入", "读档恢复历史快照，不保留未来对话");
        Check(File.Exists(Path.Combine(directory, "rain_room.json.before-save-sync")), "首次启用历史同步保留原独立历史备份");
        string before = store.Snapshot("rain_room");
        foreach (string bad in new[] { "", "null", "[null]", "[{}]", "{broken}" })
        {
            bool rejected = false;
            try { store.Restore("rain_room", bad); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && store.Snapshot("rain_room") == before, "损坏历史快照拒绝覆盖现有历史");
        }
        store.Restore("rain_room", "[]");
        Check(store.Read("rain_room").Count == 0, "新游戏使用空历史，不串入旧进度");
        var now = DateTimeOffset.UtcNow;
        var cache = new ModelLimitsStore(directory, () => now);
        var connection = new AiSettings { Mode = "api", BaseUrl = "https://example.invalid", Model = "m" };
        Check(cache.Status(connection).Contains("尚未查询"), "容量状态区分尚未查询");
        cache.Save(connection, [new("m")]);
        Check(cache.Status(connection).Contains("未提供"), "容量状态区分已查询但未提供");
        cache.Save(connection, [new("m", 1048576, 100000)]);
        Check(cache.Status(connection).Contains("已查询"), "容量状态记录查询时间与部分缺失说明");
        now = now.AddDays(2);
        Check(cache.Status(connection).Contains("过期"), "容量状态明确提示缓存过期");
        var ds = connection with { BaseUrl = "https://api.deepseek.com", Model = "deepseek-v4-pro" };
        Check(GenerationPolicy.SamplingStatus(ds, new() { Thinking = "auto" }) == "模型决定" &&
            GenerationPolicy.SamplingStatus(ds, new() { Thinking = "off" }) == "按设置请求使用", "采样方案保存值与当前请求实际采用的控制项分别显示");
    }
}
