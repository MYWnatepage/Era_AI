using System;
using System.Diagnostics;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    private NarrativePlayback narrative;
    private readonly Stopwatch narrativeClock = new();
    private int narrativeRendered;
    private string narrativeParagraph = "";
    private bool narrativePartialLine;
    private bool narrativeDrawing;
    internal bool NarrativeActive => narrative != null;

    internal void BeginNarrative(string text, bool animate = true)
    {
        FinishNarrative();
        PrintFlush(false);
        var setting = window.NarrativeSettings;
        narrative = new(text, setting.CharactersPerSecond, animate && setting.Gradual);
        narrativeRendered = 0; narrativeParagraph = ""; narrativePartialLine = false;
        narrativeClock.Restart();
        window.BeginNarrativeInput();
        PollNarrative();
    }

    internal bool PollNarrative()
    {
        // RefreshStrings may pump a timed-wait continuation while a paragraph is
        // being painted. That continuation must wait rather than render it again.
        if (narrativeDrawing) return false;
        if (narrative == null) return true;
        narrativeDrawing = true;
        try
        {
        int end = narrative.Advance(narrativeClock.Elapsed.TotalSeconds);
        if (end > narrativeRendered)
        {
            if (narrativePartialLine)
            {
                // Only the final unfinished logical paragraph is rebuilt. Earlier
                // paragraphs stay in the normal console log and keep their wrapping.
                for (int i = displayLineList.Count - 1; i >= 0; i--)
                {
                    var line = displayLineList[i];
                    foreach (var button in line.Buttons) { button.bitmapCache?.Dispose(); button.bitmapCache = null; }
                    if (line.IsLogicalLine) break;
                }
                Array.Clear(bitmapCacheArray);
                deleteLine(1); narrativePartialLine = false;
            }
            string added = narrative.Text[narrativeRendered..end];
            string[] paragraphs = (narrativeParagraph + added).Split('\n');
            for (int i = 0; i < paragraphs.Length - 1; i++) { PrintPlain(paragraphs[i]); NewLine(); }
            narrativeParagraph = paragraphs[^1];
            if (narrativeParagraph.Length > 0)
            {
                PrintPlain(narrativeParagraph); NewLine(); narrativePartialLine = true;
            }
            narrativeRendered = end;
            RefreshStrings(true);
        }
        if (narrativeRendered != narrative.Text.Length) return false;
        narrative = null; narrativeClock.Stop(); narrativeParagraph = ""; narrativePartialLine = false;
        window.EndNarrativeInput();
        return true;
        }
        finally { narrativeDrawing = false; }
    }
    internal void FinishNarrative()
    {
        if (narrative == null) return;
        narrative.RevealAll(); PollNarrative();
    }
}
