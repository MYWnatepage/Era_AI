using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

internal static class DiceEngineIntegrationTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static object? Get(object obj,string name)=>obj.GetType().GetProperty(name,Flags)?.GetValue(obj)??obj.GetType().GetField(name,Flags)?.GetValue(obj);
    internal static void Run(string assemblyPath,string gamePath,bool cancel)
    {
        Exception? failure=null;
        var thread=new Thread(()=>{try{Observe(assemblyPath,gamePath,cancel);}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(35)))throw new Exception("Dice engine UI timed out");
        if(failure!=null)throw failure;
    }
    private static void Observe(string assemblyPath,string gamePath,bool cancel)
    {
        string folder=Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving+=(_,name)=>File.Exists(Path.Combine(folder,name.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder,name.Name+".dll")):null;
        var player=AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var ai=player.GetType("MinorShift.Emuera.NativeAI.AiRuntime")!;
        bool waiting=false,settling=false,shown=false,cancelled=false,completed=false;
        Exception? failure=null;var total=Stopwatch.StartNew();string previous="";
        using var timer=new System.Windows.Forms.Timer{Interval=50};
        timer.Tick+=(_,_)=>
        {
            var form=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.GetType().FullName=="MinorShift.Emuera.Forms.MainWindow");
            if(form==null)return;
            try
            {
                var dice=Get(form,"diceAnimation");
                if(File.Exists(Path.Combine(gamePath,"dice-pass.txt")))
                {
                    if(dice!=null || ((RichTextBox)Get(form,"TextBox")!).ReadOnly)throw new Exception("Story resumed before the die released input");
                    completed=true;timer.Stop();form.Close();return;
                }
                if(dice!=null)
                {
                    double seconds=((Stopwatch)Get(form,"diceClock")!).Elapsed.TotalSeconds;
                    bool busy=(bool)ai.GetProperty("HistoryBusy",Flags)!.GetValue(null)!;
                    string state=Get(dice,"StopAt")==null?"waiting":"settling";
                    if(state!=previous){Console.WriteLine($"DICE OBSERVE {state} busy={busy} seconds={seconds:0.00}");previous=state;}
                    if((int)Get(dice,"Result")! !=15)throw new Exception("Animation changed the program's fixed roll");
                    if(busy && state=="waiting")waiting=true;
                    if(state=="settling")settling=true;
                    if((bool)dice.GetType().GetMethod("Highlighted",Flags)!.Invoke(dice,[seconds])!)shown=true;
                    if(cancel && !cancelled && busy && seconds>1.2)
                    {
                        form.GetType().GetMethod("richTextBox1_KeyDown",Flags)!.Invoke(form,[Get(form,"TextBox"),new KeyEventArgs(Keys.Escape)]);
                        cancelled=true;
                    }
                }
                if(total.Elapsed.TotalSeconds>28)
                {
                    var console=Get(form,"console")!;
                    throw new Exception($"Engine did not finish: waiting={waiting}, settling={settling}, shown={shown}, state={previous}, console={Get(console,"state")}, timerPending={Get(console,"need_settimer")}, timerEnabled={Get(Get(console,"genericTimer")!,"Enabled")}");
                }
            }
            catch(Exception e){failure=e;timer.Stop();form.Close();}
        };
        EventHandler? begin=null;begin=(_,_)=>{Application.Idle-=begin;timer.Start();};Application.Idle+=begin;
        player.EntryPoint!.Invoke(null,[new string[]{"--ExeDir",gamePath}]);
        if(failure!=null)throw failure;
        if(!completed||!waiting||!settling||!shown||(cancel&&!cancelled))throw new Exception($"Missing dice phase: completed={completed}, waiting={waiting}, settling={settling}, shown={shown}, cancelled={cancelled}");
        Console.WriteLine("PASS real D20 request: waiting, settling, fixed result and exit");
    }
}
