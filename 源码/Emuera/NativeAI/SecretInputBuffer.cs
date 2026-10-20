using System;

namespace MinorShift.Emuera.NativeAI;

internal sealed class SecretInputBuffer
{
    public bool Active { get; private set; }
    public bool Ready { get; private set; }
    private string encrypted = "";
    public void Begin() { Active = true; Ready = false; encrypted = ""; }
    public void Submit(string value)
    {
        value = value.Trim();
        if (value.Length is > 0 and < 8) throw new ArgumentException("密钥内容过短，请粘贴完整密钥后再按回车。");
        if (value.Contains('*') || value.Contains('•') || value.Contains('…') || value.AnyWhitespace())
            throw new ArgumentException("密钥包含遮蔽符号或空白，请粘贴完整密钥。");
        encrypted = SecretProtection.Protect(value);
        Ready = true;
    }
    public string Take()
    {
        if (!Active || !Ready) throw new InvalidOperationException("尚未收到密钥输入，请重新填写。");
        var value = encrypted;
        encrypted = ""; Active = false; Ready = false;
        return value;
    }
}
internal static class SecretInputText
{
    public static bool AnyWhitespace(this string value)
    {
        foreach (char c in value) if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
        return false;
    }
}
