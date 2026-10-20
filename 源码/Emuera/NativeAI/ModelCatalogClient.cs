#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.NativeAI;

internal sealed record ModelCatalogResult(bool Success, string[] Models, string Error = "", ModelLimits[]? Limits = null);
internal sealed class ModelCatalogClient(HttpClient http)
{
    public async Task<ModelCatalogResult> ReadAsync(AiSettings settings, CancellationToken cancellation)
    {
        settings.ValidateConnection();
        var key = settings.GetKey();
        if (settings.Mode == "api" && string.IsNullOrWhiteSpace(key))
            return new(false, [], "请先填写 API 密钥，再读取模型列表。");
        if (settings.Mode == "api" && key.Length < 8)
            return new(false, [], "保存的密钥内容不完整，请重新填写完整密钥。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl.TrimEnd('/') + "/models");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, [], (int)response.StatusCode switch
                {
                    401 or 403 => "读取模型列表失败，请检查密钥与模型访问权限。",
                    404 or 405 => "该地址未提供兼容的模型列表接口，请检查服务地址或服务商说明。",
                    429 => "服务限流，请稍后再读取模型列表。",
                    _ => $"读取模型列表失败（状态码 {(int)response.StatusCode}）。",
                });
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return new(false, [], "模型列表格式不兼容，服务需要提供 data 数组及模型 id。");
            var names = data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsControl))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).Take(5000).ToArray();
            var items = data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String)
                .GroupBy(item => item.GetProperty("id").GetString(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key!, group => group.First(), StringComparer.Ordinal);
            var limits = names.Select(id => ModelLimits.Parse(items[id], id)).ToArray();
            return names.Length == 0 ? new(false, [], "服务未返回可选择的模型，请检查账号权限。") : new(true, names, "", limits);
        }
        catch (OperationCanceledException) { return new(false, [], cancellation.IsCancellationRequested ? "本次请求已取消。" : "读取模型列表超时，请检查服务或增加等待时间。"); }
        catch (HttpRequestException) { return new(false, [], "无法读取模型列表，请检查服务地址与网络连接。"); }
        catch (JsonException) { return new(false, [], "模型列表不是有效的 JSON，请检查服务地址。"); }
    }
}
