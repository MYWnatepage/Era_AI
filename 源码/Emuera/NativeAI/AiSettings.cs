using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MinorShift.Emuera.NativeAI;

internal sealed record AiSettings
{
    public string Mode { get; init; } = "local";
    public string BaseUrl { get; init; } = "http://127.0.0.1:11434/v1";
    public string Model { get; init; } = "";
    public string ProtectedKey { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 180;
    public string ModeName => Mode == "api" ? "网络 API" : "本地大模型（未完成）";
    public string GetKey() => SecretProtection.Unprotect(ProtectedKey);
    public AiSettings WithServiceAddress(string address)
    {
        address = address.Trim().TrimEnd('/');
        if (address.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) address = address[..^17].TrimEnd('/');
        var next = this with { BaseUrl = address };
        next.ValidateConnection();
        bool sameService = Uri.TryCreate(BaseUrl, UriKind.Absolute, out var oldUri) &&
            Uri.Compare(oldUri, new Uri(address), UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
        return next with { Model = BaseUrl.TrimEnd('/') == address ? Model : "", ProtectedKey = sameService ? ProtectedKey : "" };
    }

    public void Validate()
    {
        ValidateConnection();
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("请填写模型名称。");
        if (Mode == "api" && string.IsNullOrWhiteSpace(GetKey())) throw new ArgumentException("API 服务需要填写密钥。");
        if (Mode == "api" && GetKey().Length < 8) throw new ArgumentException("保存的密钥内容不完整，请重新填写完整密钥。");
    }
    public void ValidateConnection()
    {
        if (Mode != "local" && Mode != "api") throw new ArgumentException("请选择有效的接入方式。");
        if (!Uri.TryCreate(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请填写有效的服务根地址，例如 http://127.0.0.1:11434/v1。");
        if (TimeoutSeconds < 10 || TimeoutSeconds > 600) throw new ArgumentException("等待时间应在 10 至 600 秒之间。");
    }
}

internal sealed class AiSettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "ai-settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public AiSettings Load()
    {
        if (!File.Exists(path)) return new AiSettings();
        try { return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path)) ?? new AiSettings(); }
        catch (JsonException) { throw new InvalidOperationException("模型设置文件损坏，请在 AI 设置中重新保存。"); }
    }
    public void Save(AiSettings settings)
    {
        settings.Validate();
        SaveConnection(settings);
    }
    public AiSettings LoadConnection(string mode)
    {
        if (mode is not ("local" or "api")) throw new ArgumentException("无效的接入方式。");
        var active = Load();
        if (active.Mode == mode) return active;
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("Connections", out var profiles) && profiles.TryGetProperty(mode, out var profile))
                return (profile.Deserialize<AiSettings>() ?? new()) with { Mode = mode };
        }
        return new AiSettings { Mode = mode, BaseUrl = mode == "local" ? "http://127.0.0.1:11434/v1" : "https://api.example.com/v1" };
    }
    public void SaveConnection(AiSettings settings)
    {
        settings.ValidateConnection();
        var connections = new System.Collections.Generic.Dictionary<string, AiSettings>();
        foreach (string mode in new[] { "local", "api" }) connections[mode] = LoadConnection(mode);
        connections[settings.Mode] = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new
        {
            settings.Mode, settings.BaseUrl, settings.Model, settings.ProtectedKey, settings.TimeoutSeconds,
            Connections = connections,
        }, Options), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

// Windows DPAPI: encrypted secrets can only be decrypted by the same Windows user.
internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false)); }
        catch { throw new InvalidOperationException("无法解密密钥，请在当前 Windows 账户下重新填写并保存。"); }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, "EraAI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows 密钥保护失败。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            Array.Clear(bytes);
        }
    }
}
