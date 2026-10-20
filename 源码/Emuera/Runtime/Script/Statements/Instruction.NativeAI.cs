using System;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.GameProc.Function;

internal sealed partial class FunctionIdentifier
{
    private sealed class NativeAIInstruction : AInstruction
    {
        private readonly FunctionCode code;
        public NativeAIInstruction(FunctionCode code)
        {
            this.code = code;
            flag = EXTENDED;
            ArgBuilder = code switch
            {
                FunctionCode.AISTART => ArgumentParser.GetNormalArgumentBuilder("SSSSIIS", 7),
                FunctionCode.AIMEMORY or FunctionCode.AICLEAR => ArgumentParser.GetArgumentBuilder(FunctionArgType.STR_EXPRESSION),
                FunctionCode.AICONNECTION or FunctionCode.AIGENERATION => ArgumentParser.GetArgumentBuilder(FunctionArgType.INT_EXPRESSION),
                _ => ArgumentParser.GetArgumentBuilder(FunctionArgType.VOID),
            };
        }
        public override void DoInstruction(ExpressionMediator exm, InstructionLine func, ProcessState state)
        {
            try
            {
                switch (code)
                {
                    case FunctionCode.AIGENERATION:
                        ConfigureGeneration(exm, (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm));
                        break;
                    case FunctionCode.AICONNECTION:
                        ConfigureConnection(exm, (int)((ExpressionArgument)func.Argument).Term.GetIntValue(exm));
                        break;
                    case FunctionCode.AISTART:
                        var args = ((ExpressionsArgument)func.Argument).ArgumentArray;
                        AiRuntime.Start(new AiRequest(args[0].GetStrValue(exm), args[1].GetStrValue(exm), args[2].GetStrValue(exm), args[3].GetStrValue(exm),
                            (int)Math.Clamp(args[4].GetIntValue(exm), 1, int.MaxValue), (int)Math.Clamp(args[5].GetIntValue(exm), 0, 8192), args[6].GetStrValue(exm)));
                        exm.VEvaluator.RESULT = 0;
                        break;
                    case FunctionCode.AIPOLL:
                        exm.VEvaluator.RESULT = AiRuntime.Poll(out var response);
                        exm.VEvaluator.RESULTS = response;
                        exm.VEvaluator.RESULTS_ARRAY[1] = AiRuntime.LastWarning;
                        break;
                    case FunctionCode.AICANCEL:
                        AiRuntime.Cancel();
                        break;
                    case FunctionCode.AICONFIG:
                        AiRuntime.Configure();
                        break;
                    case FunctionCode.AIINFO:
                        var settings = AiRuntime.Settings.Load();
                        exm.VEvaluator.RESULTS = settings.Model.Length == 0 ? "尚未设置" : settings.Model;
                        exm.VEvaluator.RESULTS_ARRAY[1] = settings.ModeName;
                        break;
                    case FunctionCode.AIMEMORY:
                        exm.VEvaluator.RESULTS = AiRuntime.Memory.Display(((ExpressionArgument)func.Argument).Term.GetStrValue(exm));
                        exm.VEvaluator.RESULT = 1;
                        break;
                    case FunctionCode.AICLEAR:
                        if (AiRuntime.Busy) throw new InvalidOperationException("请等待当前对话完成后再清空记忆。");
                        AiRuntime.Memory.Clear(((ExpressionArgument)func.Argument).Term.GetStrValue(exm));
                        exm.VEvaluator.RESULT = 1;
                        break;
                }
            }
            catch (Exception e)
            {
                exm.VEvaluator.RESULT = -1;
                exm.VEvaluator.RESULTS = e is ArgumentException or InvalidOperationException ? e.Message : "AI 操作失败，请检查配置或文件权限。";
            }
        }
        private static void ConfigureGeneration(ExpressionMediator exm, int field)
        {
            var connection = AiRuntime.Settings.Load();
            var current = AiRuntime.Generation.Load(connection);
            var limits = AiRuntime.GetModelLimits(connection);
            if (field == 0)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                string[] values = [current.PresetName, current.OutputText, current.ContextText(limits),
                    current.HistoryTurns.ToString(culture), current.Temperature.ToString("0.############################", culture), current.TopP.ToString("0.############################", culture),
                    current.FrequencyPenalty.ToString("0.############################", culture), current.PresencePenalty.ToString("0.############################", culture), current.ThinkingName,
                    GenerationPolicy.Notice(connection, current), limits.Description, current.LengthName, current.ContextSummary(limits), AiRuntime.LimitsStatus(connection),
                    GenerationPolicy.SamplingStatus(connection, current), GenerationPolicy.SamplingStatus(connection, current, true)];
                for (int i = 0; i < values.Length; i++) exm.VEvaluator.RESULTS_ARRAY[i] = values[i];
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (AiRuntime.Busy) throw new InvalidOperationException("请等待当前请求完成后再修改生成参数。");
            string input = exm.VEvaluator.RESULTS.Trim();
            if (input.Length == 0) { exm.VEvaluator.RESULT = 0; return; }
            var next = current.Edit(field, input);
            limits.Validate(next, field);
            AiRuntime.Generation.Save(next, connection);
            exm.VEvaluator.RESULTS = "";
            exm.VEvaluator.RESULT = 1;
        }
        private static void ConfigureConnection(ExpressionMediator exm, int action)
        {
            if (action is 20 or 21 or 22)
            {
                if (AiRuntime.HistoryBusy) throw new InvalidOperationException("请等待当前对话完成后再保存或恢复 AI 历史。");
                var values = exm.VEvaluator.RESULTS_ARRAY;
                if (action == 20) exm.VEvaluator.RESULTS = AiRuntime.Memory.Snapshot(exm.VEvaluator.RESULTS);
                else if (action == 21) AiRuntime.Memory.Restore(values[1], exm.VEvaluator.RESULTS);
                else AiRuntime.Memory.Append(values[1], new ConversationTurn(values[2], values[3], values[4]));
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (action == 18)
            {
                var connection = AiRuntime.Settings.Load();
                if (connection.Mode != "api") throw new InvalidOperationException("消耗统计仅适用于网络 API。");
                exm.Console.Window.ShowUsageChart(AiRuntime.Usage.SevenDays(connection));
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (action == 19) { exm.VEvaluator.RESULT = exm.Console.Window.UsageChartActive ? 0 : 1; return; }
            if (action is 14 or 17) { AiRuntime.EnsureAccount(action == 17); exm.VEvaluator.RESULT = 1; return; }
            if (action == 15) { exm.VEvaluator.RESULT = AiRuntime.PollAccount(); return; }
            if (action == 16)
            {
                var lines = AiRuntime.AccountLines();
                for (int i = 0; i < lines.Length; i++) exm.VEvaluator.RESULTS_ARRAY[i] = lines[i];
                exm.VEvaluator.RESULT = 1;
                return;
            }
            if (action == 9) { exm.Console.Window.BeginSecretInput(); return; }
            if (action == 10) { exm.Console.Window.EndSecretInput(); return; }
            if (action == 5 && exm.Console.Window.HasSecretInputSession)
            {
                string protectedKey = exm.Console.Window.TakeSecretInput();
                if (protectedKey.Length > 0)
                    AiRuntime.Settings.SaveConnection(AiRuntime.Settings.Load() with { ProtectedKey = protectedKey });
                exm.VEvaluator.RESULTS = "";
                exm.VEvaluator.RESULT = protectedKey.Length == 0 ? 0 : 1;
                return;
            }
            if (action == 8) { AiRuntime.StartModelList(); exm.VEvaluator.RESULT = 0; return; }
            if (action == 11)
            {
                exm.VEvaluator.RESULT = AiRuntime.PollModelList(out string error);
                exm.VEvaluator.RESULTS = error;
                return;
            }
            var settings = AiRuntime.Settings.Load();
            string input = exm.VEvaluator.RESULTS;
            if (action is 12 or 13)
            {
                if (AiRuntime.ModelsConnection == null || settings.Mode != AiRuntime.ModelsConnection.Mode ||
                    settings.BaseUrl != AiRuntime.ModelsConnection.BaseUrl || settings.ProtectedKey != AiRuntime.ModelsConnection.ProtectedKey)
                    throw new InvalidOperationException("连接设置已变化，请重新读取模型列表。");
                if (action == 13)
                {
                    int page = (int)Math.Clamp(exm.VEvaluator.RESULT, 0, Math.Max(0, (AiRuntime.Models.Length - 1) / 20));
                    for (int i = page * 20; i < Math.Min(AiRuntime.Models.Length, (page + 1) * 20); i++)
                        exm.Console.PrintSingleLine($"[{i + 1}] {AiRuntime.Models[i].Replace('[', '（').Replace(']', '）')}" + (AiRuntime.Models[i] == settings.Model ? "（当前）" : ""));
                    exm.VEvaluator.RESULT = AiRuntime.Models.Length;
                    return;
                }
                long index = exm.VEvaluator.RESULT - 1;
                if (index < 0 || index >= AiRuntime.Models.Length) throw new ArgumentException("请选择列表中的模型编号。");
                AiRuntime.Settings.SaveConnection(settings with { Model = AiRuntime.Models[index] });
                exm.VEvaluator.RESULT = 1;
                exm.VEvaluator.RESULTS = "";
                return;
            }
            if (action == 0)
            {
                exm.VEvaluator.RESULTS = settings.Model.Length == 0 ? "尚未设置" : settings.Model;
                exm.VEvaluator.RESULTS_ARRAY[1] = settings.ModeName;
                exm.VEvaluator.RESULTS_ARRAY[2] = settings.BaseUrl;
                exm.VEvaluator.RESULTS_ARRAY[3] = settings.ProtectedKey.Length == 0 ? "未填写" : settings.GetKey().Length < 8 ? "内容不完整，请重新填写" : "已填写（隐藏）";
                exm.VEvaluator.RESULTS_ARRAY[4] = settings.TimeoutSeconds.ToString();
                exm.VEvaluator.RESULT = settings.Mode == "local" ? 0 : 1;
                return;
            }
            if (AiRuntime.Busy) throw new InvalidOperationException("请先结束当前生成，再修改连接设置。");
            if (action is 1 or 2) settings = AiRuntime.Settings.LoadConnection(action == 1 ? "local" : "api");
            else if (action == 7) settings = settings with { ProtectedKey = "" };
            else
            {
                input = input.Trim();
                if (input.Length == 0) { exm.VEvaluator.RESULT = 0; return; }
                switch (action)
                {
                    case 3:
                        settings = settings.WithServiceAddress(input);
                        break;
                    case 4: settings = settings with { Model = input }; break;
                    case 5: settings = settings with { ProtectedKey = SecretProtection.Protect(input) }; break;
                    case 6:
                        if (!int.TryParse(input, out int seconds)) throw new ArgumentException("请输入 10～600 的整数秒数。");
                        settings = settings with { TimeoutSeconds = seconds };
                        break;
                    default: throw new ArgumentException("无效的模型连接操作。");
                }
            }
            AiRuntime.Settings.SaveConnection(settings);
            exm.VEvaluator.RESULTS = "";
            exm.VEvaluator.RESULT = 1;
        }
    }
}
