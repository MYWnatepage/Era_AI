#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MinorShift.Emuera.NativeAI;

internal sealed record TokenUsage(long Input, long Output, long? CacheHit)
{
    internal static TokenUsage? Read(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        static long? Number(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 ? n : null;
        var input = Number(usage, "prompt_tokens");
        var output = Number(usage, "completion_tokens");
        if (input == null || output == null) return null;
        var hit = Number(usage, "prompt_cache_hit_tokens");
        if (hit == null && usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            hit = Number(details, "cached_tokens");
        return new(input.Value, output.Value, hit == null ? null : Math.Min(hit.Value, input.Value));
    }
}

internal sealed record UsageDay
{
    public string Account { get; init; } = "";
    public string Date { get; init; } = "";
    public long Input { get; set; }
    public long Output { get; set; }
    public long Requests { get; set; }
    public long MissingUsage { get; set; }
    public long Unpriced { get; set; }
    public decimal CostMin { get; set; }
    public decimal CostMax { get; set; }
}

internal sealed record UsagePoint(DateOnly Day, UsageDay? Record);

internal static class ApiAccount
{
    internal static bool IsDeepSeek(AiSettings settings) => settings.Mode == "api" &&
        Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath.TrimEnd('/') is "" or "/v1");

    internal static string Id(AiSettings settings)
    {
        var uri = new Uri(settings.BaseUrl.TrimEnd('/'));
        var service = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + (IsDeepSeek(settings) ? "" : uri.AbsolutePath.TrimEnd('/'));
        // Persist only an opaque identity, never the key or any conversation text.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service + "\n" + settings.GetKey())));
    }
}

internal sealed class AccountUsageStore(string directory, Func<DateTimeOffset>? clock = null)
{
    private readonly string path = Path.Combine(directory, "api-usage.json");
    private readonly string mutexName = "Local\\EraAIUsage-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Path.Combine(directory, "api-usage.json")).ToUpperInvariant())));
    private readonly object gate = new();
    private string Today => (clock?.Invoke() ?? DateTimeOffset.Now).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private List<UsageDay> Load()
    {
        if (!File.Exists(path)) return [];
        var days = JsonSerializer.Deserialize<List<UsageDay>>(File.ReadAllText(path));
        if (days == null || days.Any(x => x == null || string.IsNullOrWhiteSpace(x.Account) || string.IsNullOrWhiteSpace(x.Date) ||
            x.Input < 0 || x.Output < 0 || x.Requests < 0 || x.MissingUsage < 0 || x.Unpriced < 0 || x.CostMin < 0 || x.CostMax < x.CostMin))
            throw new JsonException("用量记录格式不正确。");
        return days;
    }

    private T Locked<T>(Func<T> operation)
    {
        lock (gate)
        {
            using var mutex = new Mutex(false, mutexName);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("用量记录正在被其他窗口使用，请稍后重试。");
                return operation();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    internal UsageDay Read(AiSettings settings)
    {
        return Locked(() => Load().FirstOrDefault(x => x.Account == ApiAccount.Id(settings) && x.Date == Today) ?? new());
    }

    internal UsagePoint[] SevenDays(AiSettings settings)
    {
        return Locked(() =>
        {
            var account = ApiAccount.Id(settings);
            var today = DateOnly.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var days = Load();
            return Enumerable.Range(-6, 7).Select(offset =>
            {
                var date = today.AddDays(offset);
                var day = days.FirstOrDefault(x => x.Account == account && x.Date == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return new UsagePoint(date, day);
            }).ToArray();
        });
    }

    internal void Record(AiSettings settings, TokenUsage? usage, string responseModel)
    {
        if (settings.Mode != "api") return;
        Locked(() =>
        {
            var days = Load();
            string account = ApiAccount.Id(settings), today = Today;
            var day = days.FirstOrDefault(x => x.Account == account && x.Date == today);
            if (day == null) { day = new() { Account = account, Date = today }; days.Add(day); }
            day.Requests++;
            if (usage == null) day.MissingUsage++;
            else
            {
                day.Input += usage.Input;
                day.Output += usage.Output;
                if (Estimate(settings, responseModel, usage) is { } estimate)
                {
                    day.CostMin += estimate.Min;
                    day.CostMax += estimate.Max;
                }
                else day.Unpriced++;
            }
            Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(days, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, path, true);
            return 0;
        });
    }

    // Official CNY tariffs checked 2026-10-19:
    // https://api-docs.deepseek.com/zh-cn/quick_start/pricing/
    // A range covers off-peak/peak (including holiday uncertainty), not an exact bill.
    internal static (decimal Min, decimal Max)? Estimate(AiSettings settings, string model, TokenUsage usage)
    {
        if (!ApiAccount.IsDeepSeek(settings)) return null;
        var prices = model.ToLowerInvariant() switch
        {
            "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp" => (Hit: .02m, Miss: 1m, Output: 4m),
            "deepseek-v4-pro" => (Hit: .15m, Miss: 4.5m, Output: 13.5m),
            _ => (Hit: 0m, Miss: 0m, Output: 0m),
        };
        if (prices.Output == 0) return null;
        decimal minHit = usage.CacheHit ?? usage.Input, maxHit = usage.CacheHit ?? 0;
        decimal low = (minHit * prices.Hit + (usage.Input - minHit) * prices.Miss + usage.Output * prices.Output) / 1000000m;
        decimal high = (maxHit * prices.Hit + (usage.Input - maxHit) * prices.Miss + usage.Output * prices.Output) * 2 / 1000000m;
        return (low, high);
    }

    internal static string CostText(UsageDay day)
    {
        if (day.MissingUsage > 0 || day.Unpriced > 0) return "暂无法完整估算（部分请求缺少用量或适用价格）";
        if (day.Requests == 0) return "¥0（今日暂无记录）";
        return string.Create(CultureInfo.InvariantCulture, $"¥{day.CostMin:0.000000}～{day.CostMax:0.000000}");
    }
}
