#nullable enable
using System;
using System.Linq;

namespace MinorShift.Emuera.NativeAI;

internal sealed record LocalActionText(string Narrative, string Summary);

// Stable rotation makes previews/load/retry repeatable without using the gameplay
// random generator, calling a model or adding presentation counters to a save.
internal static class LocalActionNarration
{
    private static readonly string[] Sleep =
    [
        "你在{地点}安顿下来，暂且放下手边的事情，慢慢进入梦乡。",
        "你在{地点}收拢心绪，把注意力从眼前的事情上移开。过了一阵，你睡着了。",
        "你在{地点}准备入眠，不再继续考虑接下来的安排，渐渐睡去。",
        "你在{地点}停下今天的活动，安静地待了一会儿，随后进入睡眠。",
        "你在{地点}安静下来，思绪渐渐放缓，在不知不觉中睡着了。",
        "你在{地点}把接下来的事情暂放一旁，让这一段时间在睡眠中度过。"
    ];
    private static readonly string[] Rest =
    [
        "你在{地点}暂时停下活动，放缓节奏，给自己留出片刻喘息的时间。",
        "你在{地点}歇了一会儿，没有急着继续行动，只让这段时间慢慢过去。",
        "你在{地点}安静地休息片刻，把注意力从眼前的事务上移开。",
        "你在{地点}暂停了手边的事情。等这段休息结束，再考虑接下来的安排。",
        "你在{地点}稍作停留，整理了一下思绪，没有立刻开始新的行动。",
        "你在{地点}给自己留出一小段空当，暂且不赶路，也不忙着做别的事。"
    ];
    private static readonly string[] SleepSummaries =
    [
        "你在{地点}睡过一觉，仍停留在这里。",
        "这次睡眠已经结束，你还在{地点}。",
        "你在{地点}结束了这次睡眠，尚未离开。"
    ];
    private static readonly string[] RestSummaries =
    [
        "你在{地点}歇了片刻，暂时没有开始新的行动。",
        "这段短暂的休息已经结束，你仍停留在{地点}。",
        "你在{地点}给自己留出了一段休息时间。"
    ];
    private static readonly string[] Bonus =
    [
        "这里的环境让你能更从容地休息。",
        "在这里休息，比平常更容易放松一些。",
        "此处的条件让这段休息更加充分。"
    ];

    internal static LocalActionText Build(InventoryState state, bool sleep, bool bonus)
    {
        string location = state.Scene?.Location ?? "当前位置";
        var bodies = sleep ? Sleep : Rest;
        var summaries = sleep ? SleepSummaries : RestSummaries;
        int revision = Math.Max(0, state.Revision);
        string Fill(string template) => template.Replace("{地点}", location, StringComparison.Ordinal);
        string narrative = Fill(bodies[revision % bodies.Length]);
        var present = state.Scene?.PresentCharacters.Where(id => id != "player").Select(id => state.Characters[id]).ToArray() ?? [];
        narrative += present.Length == 0 ? "这段时间，你独自休息。" : "这里还有" + string.Join("、", present) + "。";
        if (bonus) narrative += Bonus[revision / bodies.Length % Bonus.Length];
        return new(narrative, Fill(summaries[revision % summaries.Length]));
    }
}
