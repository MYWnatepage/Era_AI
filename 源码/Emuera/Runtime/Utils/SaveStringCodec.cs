using System;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

// Legacy text saves use one physical line per array element. Encode only values
// that would split an element, terminate an array, or collide with this prefix.
internal static class SaveStringCodec
{
    private const string Prefix = "__ERA_AI_TEXT_V1__:";
    internal static string Encode(string value)
    {
        if (value == null) return "";
        if (!value.Contains('\n') && !value.Contains('\r') && value != "__FINISHED" &&
            value != "__EMU_SEPARATOR__" && !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }
    internal static string Decode(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value[Prefix.Length..])); }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { return value; }
    }
}
