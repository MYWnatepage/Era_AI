using System;
using System.Drawing;
using MinorShift.Emuera.NativeDisplay;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.GameProc.Function;

internal sealed partial class FunctionIdentifier
{
    private sealed class NativeDisplayInstruction : AInstruction
    {
        private readonly FunctionCode code;
        public NativeDisplayInstruction(FunctionCode code)
        {
            this.code = code;
            flag = EXTENDED;
            ArgBuilder = ArgumentParser.GetArgumentBuilder(code == FunctionCode.NARRATIVE ? FunctionArgType.STR_EXPRESSION :
                code is FunctionCode.DISPLAYCONFIG or FunctionCode.DISPLAYTYPE or FunctionCode.ESCAPE ? FunctionArgType.INT_EXPRESSION : FunctionArgType.VOID);
        }
        public override void DoInstruction(ExpressionMediator exm, InstructionLine func, ProcessState state)
        {
            if (code == FunctionCode.NARRATIVE)
            {
                exm.Console.BeginNarrative(((ExpressionArgument)func.Argument).Term.GetStrValue(exm));
                return;
            }
            if (code == FunctionCode.NARRATIVEPOLL)
            {
                exm.VEvaluator.RESULT = exm.Console.PollNarrative() ? 1 : 0;
                return;
            }
            if (code == FunctionCode.DISPLAYTYPE && ((ExpressionArgument)func.Argument).Term.GetIntValue(exm) is >= 8 and <= 10)
            {
                int field = (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
                var window = exm.Console.Window;
                if (field == 8)
                {
                    var value = window.NarrativeSettings;
                    exm.VEvaluator.RESULTS = value.ModeName;
                    exm.VEvaluator.RESULTS_ARRAY[1] = value.CharactersPerSecond.ToString();
                    exm.VEvaluator.RESULTS_ARRAY[2] = "非流式";
                    exm.VEvaluator.RESULT = value.Gradual ? 1 : 0;
                }
                else if (field == 9) { window.ShowNarrativeEditor(); exm.VEvaluator.RESULT = 1; }
                else exm.VEvaluator.RESULT = window.NarrativeEditing ? 0 : 1;
                return;
            }
            if (code == FunctionCode.ESCAPE)
            {
                exm.Console.NextEscapeValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
                return;
            }
            if (code == FunctionCode.DISPLAYSTATE)
            {
                exm.VEvaluator.RESULT = DisplayRuntime.Editing ? 0 : 1;
                return;
            }
            var global = (long[])GlobalStatic.IdentifierDictionary.GetVariableToken("GLOBAL", null, true).GetArray();
            int Rgb(int start) => ((int)Math.Clamp(global[start], 0, 255) << 16) | ((int)Math.Clamp(global[start + 1], 0, 255) << 8) | (int)Math.Clamp(global[start + 2], 0, 255);
            int fg = global[200] switch { 1 => 0xDCDCDC, 2 => 0xB4F0FF, 3 => 0xF5EBDC, 4 => Rgb(210), _ => ColorSafety.Pack(Config.ForeColor) };
            int bg = global[201] switch { 1 => 0x0F0F12, 2 => 0x0A121E, 3 => Rgb(220), _ => ColorSafety.Pack(Config.BackColor) };
            exm.Console.Window.SyncWindowDrawingWidth();
            DisplayRuntime.Apply(exm.Console, new DisplayTheme(fg, bg));
            if (code == FunctionCode.DISPLAYAPPLY) exm.Console.Window.Music.Ensure();
            if (code == FunctionCode.DISPLAYTYPE)
            {
                int mode = (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
                var error = mode is 6 or 7
                    ? exm.Console.Window.SetGameWindowSize(exm.VEvaluator.RESULTS, mode == 7)
                    : TypographyRuntime.Update(exm.Console, mode, exm.VEvaluator.RESULTS, exm.VEvaluator.RESULT);
                exm.VEvaluator.RESULT = error.Length == 0 ? 1 : -1;
                exm.VEvaluator.RESULTS = error;
                return;
            }
            if (code == FunctionCode.DISPLAYCONFIG)
                DisplayRuntime.Configure(exm.Console, (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm));
            else if (code == FunctionCode.DISPLAYINFO)
            {
                var theme = DisplayRuntime.Current;
                exm.VEvaluator.RESULTS = $"RGB({theme.TextColor.R},{theme.TextColor.G},{theme.TextColor.B})";
                exm.VEvaluator.RESULTS_ARRAY[1] = $"RGB({theme.BackgroundColor.R},{theme.BackgroundColor.G},{theme.BackgroundColor.B})";
                var focus = ColorSafety.Selection(theme);
                exm.VEvaluator.RESULTS_ARRAY[2] = $"RGB({focus.R},{focus.G},{focus.B})";
                exm.VEvaluator.RESULTS_ARRAY[3] = Config.FontName;
                exm.VEvaluator.RESULTS_ARRAY[4] = Config.FontSize.ToString();
                exm.VEvaluator.RESULTS_ARRAY[5] = Config.LineHeight.ToString();
                var size = exm.Console.Window.WindowContentSize;
                var limit = exm.Console.Window.WindowContentLimit;
                exm.VEvaluator.RESULTS_ARRAY[6] = size.Width.ToString();
                exm.VEvaluator.RESULTS_ARRAY[7] = size.Height.ToString();
                exm.VEvaluator.RESULTS_ARRAY[8] = exm.Console.Window.WindowState == System.Windows.Forms.FormWindowState.Maximized ? "最大化" : "普通窗口";
                exm.VEvaluator.RESULTS_ARRAY[9] = limit.Width.ToString();
                exm.VEvaluator.RESULTS_ARRAY[10] = limit.Height.ToString();
            }
        }
    }
}
