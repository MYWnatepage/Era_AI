using System;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;

namespace MinorShift.Emuera.GameProc.Function;

internal sealed partial class FunctionIdentifier
{
    private sealed class NativeMusicInstruction : AInstruction
    {
        public NativeMusicInstruction()
        {
            flag = EXTENDED;
            ArgBuilder = ArgumentParser.GetArgumentBuilder(FunctionArgType.INT_EXPRESSION);
        }
        public override void DoInstruction(ExpressionMediator exm, InstructionLine func, ProcessState state)
        {
            var window = exm.Console.Window;
            var music = window.Music;
            music.Ensure();
            int command = (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
            string error = "";
            switch (command)
            {
                case 0:
                    exm.VEvaluator.RESULTS_ARRAY[0] = music.Settings.Enabled ? "开" : "关";
                    exm.VEvaluator.RESULTS_ARRAY[1] = music.Settings.Volume.ToString();
                    exm.VEvaluator.RESULTS_ARRAY[2] = music.TrackName;
                    exm.VEvaluator.RESULTS_ARRAY[3] = music.Status;
                    exm.VEvaluator.RESULTS_ARRAY[4] = music.Settings.BuiltInTrack.ToString();
                    exm.VEvaluator.RESULT = music.Settings.Volume;
                    return;
                case 11: error = exm.VEvaluator.RESULT is < 0 or > 1 ? "请选择列表中的内置曲目。" : music.SelectBuiltIn((int)exm.VEvaluator.RESULT); break;
                case 1: error = music.Toggle(); break;
                case 2: error = music.SelectTrack(""); break;
                case 3: error = music.SelectTrack(exm.VEvaluator.RESULTS); break;
                case 4: error = exm.VEvaluator.RESULT is < 0 or > 100 ? "请输入 0～100 的整数。" : music.SetVolume((int)exm.VEvaluator.RESULT); break;
                case 5: window.ShowMusicVolumeEditor(); break;
                case 6: exm.VEvaluator.RESULT = window.MusicVolumeEditing ? 0 : 1; return;
                case 7: window.MusicShortcuts = true; return;
                case 8: window.MusicShortcuts = false; window.MusicSubmenuInput = false; return;
                case 10: window.MusicSubmenuInput = true; return;
                case 9: error = music.SetVolume(Math.Clamp(music.Settings.Volume + (exm.VEvaluator.RESULT > 0 ? 10 : -10), 0, 100)); break;
                default: error = "未知音乐设置操作。"; break;
            }
            exm.VEvaluator.RESULT = error.Length == 0 ? 1 : -1;
            exm.VEvaluator.RESULTS = error;
        }
    }
}
