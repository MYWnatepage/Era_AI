#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.NativeAI;

internal static class ActionNarrative
{
    internal static string Check(string story, ScenarioActionOutcome outcome, out bool replaced)
    {
        replaced = false;
        try
        {
            foreach (string pattern in outcome.QuantityPatterns)
            {
                var rule = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                foreach (Match match in rule.Matches(story))
                    if (ReadNumber(match.Groups["quantity"].Value) != outcome.Quantity)
                    { replaced = true; return outcome.Story; }
            }
        }
        catch (RegexMatchTimeoutException) { replaced = true; return outcome.Story; }
        return story;
    }
    private static decimal ReadNumber(string text)
    {
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)) return number;
        decimal total = 0, digit = 0;
        foreach (char c in text)
        {
            int value = c switch { '零' or '〇' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4,
                '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => -1 };
            if (value < 0) return -1;
            if (value < 10) digit = value;
            else { total += (digit == 0 ? 1 : digit) * value; digit = 0; }
        }
        return total + digit;
    }
}
