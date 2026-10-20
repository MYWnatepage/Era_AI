using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    public bool SensitiveInput { get; set; }
    public long? NextEscapeValue { get; set; }
    private long? escapeValue;
    public void NavigateBack()
    {
        if (AiRuntime.Busy) { AiRuntime.Cancel(); return; }
        if (state != ConsoleState.WaitInput) return;
        if (inputReq.InputType == InputType.StrValue)
            PressEnterKey(false, "", false);
        else if (inputReq.InputType == InputType.IntValue && escapeValue is long value && value >= 0)
            PressEnterKey(false, value.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
        else if (!inputReq.NeedValue && inputReq.Timelimit <= 0)
            PressEnterKey(false, "", false);
    }
}
