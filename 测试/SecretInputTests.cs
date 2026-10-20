using MinorShift.Emuera.NativeAI;

internal static class SecretInputTests
{
    public static void Run()
    {
        static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("密钥输入测试失败：" + label);
            Console.WriteLine("通过：" + label);
        }
        const string fake = "sk-fake-regression-secret-0123456789";
        var buffer = new SecretInputBuffer();
        buffer.Begin(); buffer.Submit(fake);
        string encrypted = buffer.Take();
        Check(SecretProtection.Unprotect(encrypted) == fake, "完整密钥经独立加密缓冲传递，无截断");
        Check(!buffer.Active && !buffer.Ready, "取走密钥后清除输入会话状态");
        buffer.Begin();
        try { buffer.Submit("4"); throw new Exception("菜单编号被当作密钥"); }
        catch (ArgumentException) { Check(!buffer.Ready, "拒绝单字符菜单编号覆盖密钥"); }
        buffer.Submit("");
        Check(buffer.Take() == "", "取消密钥输入不产生替换值");
        buffer.Begin();
        try { buffer.Submit("sk-fake*****masked"); throw new Exception("接受遮蔽值"); }
        catch (ArgumentException) { Check(!buffer.Ready, "遮蔽密钥不写入配置"); }
    }
}
