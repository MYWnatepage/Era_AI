using System.Drawing;
using MinorShift.Emuera.NativeDisplay;

internal static class GameColorEditorTests
{
    public static void Run()
    {
        static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("游戏内配色页测试失败：" + label);
            Console.WriteLine("通过：" + label);
        }
        DisplayTheme visible = new();
        DisplayTheme? saved = null;
        bool? completed = null;
        var editor = new GameColorEditor(visible, 0, theme => visible = theme, theme => saved = theme, result => completed = result);
        editor.Command(1);
        editor.PreviewInput("240");
        Check(editor.Value(0) == 240 && visible.TextColor.R == 240 && editor.EnteringValue, "RGB 输入在回车前实时预览");
        editor.Submit("256");
        Check(editor.Value(0) == 240 && editor.EnteringValue, "越界输入不修改通道且可以重新输入");
        editor.Submit("200");
        Check(editor.Value(0) == 200 && !editor.EnteringValue, "输入后返回当前配色页");
        editor.Change(0, 0); editor.Change(1, 0); editor.Change(2, 0);
        Check(!editor.Candidate.IsValid && visible.IsValid, "危险候选颜色保留，页面继续使用安全配色");
        editor.Command(8);
        Check(saved == null && completed == null, "低对比度不能保存或退出");
        editor.Command(7); editor.Command(8);
        Check(saved == new DisplayTheme(0x302B25, 0xF4EBDD) && completed == true, "安全预设保存并返回");
        completed = null;
        var cancel = new GameColorEditor(new(), 1, _ => { }, _ => throw new Exception("取消不应保存"), result => completed = result);
        cancel.Command(4);
        Check(cancel.Target == 2, "背景页切换到焦点颜色");
        cancel.Command(4);
        Check(cancel.Target == 0, "同一页面切换文字和背景");
        cancel.Command(2); cancel.Escape();
        Check(!cancel.EnteringValue && completed == null, "Esc 离开数值输入");
        cancel.Wheel(Point.Empty, -120);
        Check(cancel.Value(1) == 191, "滚轮按一个色阶微调当前通道");
        cancel.Escape();
        Check(completed == false, "取消返回且不保存");
        var focus = new GameColorEditor(new(), 2, _ => { }, _ => { }, _ => { });
        Check(focus.Value(0) == 255 && focus.Value(1) == 255, "首次使用沿用自动焦点颜色");
        focus.Change(0, 0); focus.Change(1, 255); focus.Change(2, 255);
        Check(focus.Candidate.IsValid && ColorSafety.Selection(focus.Candidate).ToArgb() == Color.Cyan.ToArgb(), "自定义焦点颜色生效");
        focus.Change(0, 192); focus.Change(1, 192); focus.Change(2, 192);
        Check(!focus.Candidate.IsValid, "焦点颜色不能与普通文字相同");
        Check(new DisplayTheme(0xC0C0C0, 0, 0).Safe().FocusRgb == -1, "损坏或不可读焦点配置回退自动颜色");
    }
}
