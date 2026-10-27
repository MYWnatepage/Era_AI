using System.Drawing;
using System.Numerics;
using MinorShift.Emuera.NativeDisplay;

internal static class DiceAnimationTests
{
    internal static void Run()
    {
        static void Check(bool ok,string text) { if(!ok)throw new Exception(text);Console.WriteLine("PASS dice: "+text); }
        foreach(var (sides,faces) in DiceGeometry.Models)
        {
            Check(faces.Length==sides && faces.Select(f=>f.Value).Order().SequenceEqual(Enumerable.Range(1,sides)),$"D{sides}: exact polygon count and unique values");
            Check(faces.All(f=>f.Points.All(p=>Math.Abs(Vector3.Dot(p-f.Center,f.Normal))<.0001) && f.Points.All(p=>float.IsFinite(p.X))),$"D{sides}: coplanar, finite vertices");
            if(sides!=4)Check(faces.All(f=>f.Value+faces.MinBy(g=>Vector3.Dot(f.Normal,g.Normal))!.Value==sides+1),$"D{sides}: opposite faces sum correctly");
        }
        for(int result=1;result<=20;result++)
        {
            var dice=new D20Animation(result,"固定结果 "+result);
            Check(!dice.StopAt.HasValue && !dice.Finished(3600) && !dice.Highlighted(3600),"waiting never reveals or expires result "+result);
            Check(Math.Abs(Quaternion.Dot(dice.Orientation(10),dice.Orientation(11)))<.999,"waiting rotation continues after initial throw");
            dice.Complete(3600,true);dice.Complete(3601,false);
            Check(dice.StopAt==3600 && dice.Succeeded && !dice.Finished(3600.1),"completion is idempotent and result remains visible");
            var face=DiceGeometry.Models[20].Single(f=>f.Value==result);
            Check(Vector3.Transform(face.Normal,dice.Orientation(3602)).Z>.9999f && dice.Highlighted(3602) && dice.Finished(3602) && dice.Result==result,"settles on the original result "+result);
        }
        foreach(bool succeeded in new[]{true,false})
        {
            var dice=new D20Animation(7,"D20：7 / 固定判定");
            dice.RequestCancel();dice.RequestCancel();dice.Complete(0,succeeded);
            Check(dice.CancelRequested && dice.Result==7 && dice.Finished(2),"cancel/failure uses original result and terminates");
        }
        foreach(int invalid in new[]{0,21,-1})
        {try {new D20Animation(invalid,"");throw new Exception("invalid result accepted");}catch(ArgumentOutOfRangeException){}}
        using var font=new Font("Microsoft YaHei UI",24,FontStyle.Regular,GraphicsUnit.Pixel);
        foreach(var size in new[]{new Size(320,240),new Size(640,360),new Size(1024,640),new Size(1920,1080)})
        foreach(var theme in new[]{new DisplayTheme(0xdcdcdc,0x0f0f12,0xffff00),new DisplayTheme(0x202020,0xfafafa,0x0050aa)})
        {
            var dice=new D20Animation(16,"D20：16 / 固定结果");
            using var bitmap=new Bitmap(size.Width,size.Height);using var graphics=Graphics.FromImage(bitmap);
            dice.Draw(graphics,new(Point.Empty,size),font,theme,32,30,false);
            Check(new Rectangle(Point.Empty,size).Contains(dice.CancelBounds),"cancel target stays inside resized canvas");
            dice.Complete(30,true);dice.Draw(graphics,new(Point.Empty,size),font,theme,32,31,true);
            Check(dice.CancelBounds.IsEmpty,"settled die cannot cancel an already completed response");
            if(size.Width==1024 && theme.BackgroundRgb==0x0f0f12 && Environment.GetEnvironmentVariable("ERA_AI_DICE_PREVIEW") is string path)bitmap.Save(path);
        }
    }
}
