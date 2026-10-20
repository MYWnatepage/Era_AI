using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.NativeAI;

internal sealed record BalanceResult(bool Success, string Text, bool Cancelled = false);

internal sealed class AccountBalanceClient(HttpClient http)
{
    internal async Task<BalanceResult> ReadAsync(AiSettings settings, CancellationToken cancellation)
    {
        if (!ApiAccount.IsDeepSeek(settings)) return new(false, "该平台暂不支持查询，请到官网查看");
        settings.ValidateConnection();
        string key = settings.GetKey();
        if (key.Length < 8) return new(false, "请先填写完整 API 密钥");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        // Restrict both the host and path; never forward credentials to redirects.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(settings.BaseUrl), "/user/balance"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, (int)response.StatusCode switch
            {
                401 or 403 => "查询失败，请检查密钥与账户权限",
                429 => "查询过于频繁，请稍后刷新",
                _ => $"查询失败（状态码 {(int)response.StatusCode}）",
            });
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array)
                return new(false, "查询失败，余额数据格式不兼容");
            var amounts = new List<string>();
            foreach (var item in infos.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("total_balance", out var total) || total.ValueKind != JsonValueKind.String ||
                    !decimal.TryParse(total.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) continue;
                if (currency.GetString() is not ("CNY" or "USD")) continue;
                amounts.Add(amount.ToString("0.00######", CultureInfo.InvariantCulture) + " " + currency.GetString());
            }
            return amounts.Count == 0 ? new(false, "查询失败，服务未返回可用余额") : new(true, string.Join(" / ", amounts));
        }
        catch (OperationCanceledException) { return new(false, cancellation.IsCancellationRequested ? "查询已取消，可手动刷新" : "查询超时，可手动刷新", cancellation.IsCancellationRequested); }
        catch (HttpRequestException) { return new(false, "查询失败，请检查网络后刷新"); }
        catch (JsonException) { return new(false, "查询失败，余额数据格式不兼容"); }
    }
}
