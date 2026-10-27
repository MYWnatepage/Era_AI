#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private D20Animation? diceAnimation;
    private System.Windows.Forms.Timer? diceTimer;
    private readonly Stopwatch diceClock=new();
    private bool diceHovered,diceReturnReadOnly;
    internal bool DiceAnimationActive=>diceAnimation!=null;
    internal void BeginD20Animation(int result,string summary)
    {
        EndD20Animation();
        diceAnimation=new(result,summary);
        diceReturnReadOnly=richTextBox1.ReadOnly;
        richTextBox1.ReadOnly=true;
        diceClock.Restart();
        diceTimer=new System.Windows.Forms.Timer {Interval=33};
        diceTimer.Tick+=(_,_)=>mainPicBox.Invalidate();
        diceTimer.Start();
        mainPicBox.Invalidate();
    }
    // AIPOLL releases the same completed response only after the die has settled and been visible.
    internal bool FinishD20Animation(bool succeeded)
    {
        if(diceAnimation==null)return true;
        double seconds=diceClock.Elapsed.TotalSeconds;
        diceAnimation.Complete(seconds,succeeded);
        if(!diceAnimation.Finished(seconds))return false;
        EndD20Animation();return true;
    }
    private void EndD20Animation()
    {
        diceTimer?.Stop();diceTimer?.Dispose();diceTimer=null;
        if(diceAnimation!=null)richTextBox1.ReadOnly=diceReturnReadOnly;
        diceAnimation=null;diceHovered=false;diceClock.Stop();
        if(!IsDisposed)mainPicBox.Invalidate();
    }
    private bool DrawD20Animation(Graphics graphics)
    {
        if(diceAnimation==null)return false;
        // Preserve the engine's paint lifecycle (timed waits, refresh flags and input colors).
        // The native animation then covers it inside the same buffered paint operation.
        console?.OnPaint(graphics);
        diceAnimation.Draw(graphics,mainPicBox.ClientRectangle,richTextBox1.Font,DisplayRuntime.Current,Runtime.Config.Config.LineHeight,diceClock.Elapsed.TotalSeconds,diceHovered);
        return true;
    }
    private void CancelD20Request()
    {
        if(diceAnimation==null || diceAnimation.StopAt.HasValue || diceAnimation.CancelRequested)return;
        diceAnimation.RequestCancel();AiRuntime.Cancel();mainPicBox.Invalidate();
    }
    private bool D20MouseDown(MouseEventArgs e)
    {
        if(diceAnimation==null)return false;
        if(e.Button==MouseButtons.Left && diceAnimation.CancelBounds.Contains(e.Location))CancelD20Request();
        richTextBox1.Focus();return true;
    }
    private bool D20MouseMove(Point point)
    {
        if(diceAnimation==null)return false;
        diceHovered=diceAnimation.CancelBounds.Contains(point);mainPicBox.Invalidate();return true;
    }
}
