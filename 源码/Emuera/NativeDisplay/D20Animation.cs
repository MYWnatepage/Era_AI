#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace MinorShift.Emuera.NativeDisplay;

// Presentation accepts an already resolved roll. It never chooses or commits game outcomes.
internal sealed class D20Animation
{
    internal const double SettleSeconds=.7, ResultSeconds=.9;
    internal int Result { get; }
    internal string Summary { get; }
    internal double? StopAt { get; private set; }
    internal bool Succeeded { get; private set; }
    internal bool CancelRequested { get; private set; }
    internal bool Highlighted(double seconds)=>StopAt is double at && seconds>=at+SettleSeconds;
    internal bool Finished(double seconds)=>StopAt is double at && seconds>=at+SettleSeconds+ResultSeconds;
    internal Rectangle CancelBounds { get; private set; }
    private Quaternion stopOrientation;
    private readonly DiceFace[] faces=DiceGeometry.Models[20];
    private readonly Quaternion target;
    internal D20Animation(int result,string summary)
    {
        if (result is <1 or >20) throw new ArgumentOutOfRangeException(nameof(result));
        Result=result; Summary=summary; target=faces.Single(f=>f.Value==result).Target;
    }
    private static Quaternion Spin(double seconds)
    {
        // Fast initial throw smoothly slows to a gentle, continuous waiting rotation.
        float angle=(float)((1.3*seconds+7*(1-Math.Exp(-2*seconds)))%(Math.PI*20));
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY,angle)*Quaternion.CreateFromAxisAngle(Vector3.UnitX,angle*.67f)*Quaternion.CreateFromAxisAngle(Vector3.UnitZ,angle*.31f));
    }
    internal Quaternion Orientation(double seconds)
    {
        if (StopAt is not double at) return Spin(seconds);
        float t=(float)Math.Clamp((seconds-at)/SettleSeconds,0,1);
        return Quaternion.Slerp(stopOrientation,target,t*t*(3-2*t));
    }
    internal void Complete(double seconds,bool succeeded)
    {
        if (StopAt.HasValue) return;
        stopOrientation=Spin(seconds); StopAt=seconds; Succeeded=succeeded;
    }
    internal void RequestCancel()=>CancelRequested=true;
    private static Color Mix(Color from,Color to,float t)=>Color.FromArgb((int)(from.R+(to.R-from.R)*t),(int)(from.G+(to.G-from.G)*t),(int)(from.B+(to.B-from.B)*t));
    internal void Draw(Graphics graphics,Rectangle area,Font original,DisplayTheme theme,int lineHeight,double seconds,bool hovered)
    {
        var saved=graphics.Save();
        try
        {
            graphics.Clear(theme.BackgroundColor);
            graphics.SmoothingMode=SmoothingMode.AntiAlias;
            int line=Math.Max(12,Math.Min(lineHeight,Math.Max(12,area.Height/13)));
            using var font=new Font(original.FontFamily,Math.Max(10,Math.Min(original.Size,line*.8f)),FontStyle.Regular,GraphicsUnit.Pixel);
            int margin=Math.Max(12,line/2), width=Math.Max(1,Math.Min(820,area.Width-2*margin));
            int x=area.Left+(area.Width-width)/2;
            int stageHeight=Math.Max(40,Math.Min(300,area.Height-line*6-2*margin));
            int total=stageHeight+line*5;
            int y=Math.Max(area.Top+margin,area.Top+(area.Height-total)/2);
            Color accent=ColorSafety.Selection(theme);
            using var rule=new Pen(Mix(theme.BackgroundColor,theme.TextColor,.55f)){DashStyle=DashStyle.Dash};
            void Text(string text,Rectangle bounds,Color? color=null,TextFormatFlags flags=TextFormatFlags.Left)=>TextRenderer.DrawText(graphics,text,font,bounds,color??theme.TextColor,theme.BackgroundColor,flags|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
            graphics.DrawLine(rule,x,y,x+width,y);
            Text("D20 掷骰",new(x,y+line/2,width,line),null,TextFormatFlags.HorizontalCenter);
            y+=line*2; graphics.DrawLine(rule,x,y,x+width,y);
            DrawDie(graphics,new Rectangle(x,y,width,stageHeight),theme,seconds);
            y+=stageHeight; graphics.DrawLine(rule,x,y,x+width,y); y+=line/2;
            bool highlighted=Highlighted(seconds);
            string phase=highlighted?(Succeeded?"掷骰完成":"使用原骰点结算"):StopAt.HasValue?"正在停稳…":CancelRequested?"正在中止请求…":"正在等待回应…";
            Text(phase,new(x,y,width*2/3,line));
            Text(highlighted?$"点数：{Result}":"点数：…",new(x+width*2/3,y,width/3,line),highlighted?accent:null,TextFormatFlags.Right);
            y+=line;
            if (highlighted) Text(Summary,new(x,y,width,line));
            else Text(StopAt.HasValue?"即将显示行动结果":"[Esc] 中止本次请求",new(x,y,width,line),hovered?accent:null);
            CancelBounds=StopAt.HasValue?Rectangle.Empty:new(x,y,Math.Min(width,TextRenderer.MeasureText("[Esc] 中止本次请求",font).Width),line);
        }
        finally {graphics.Restore(saved);}
    }
    private void DrawDie(Graphics graphics,Rectangle stage,DisplayTheme theme,double seconds)
    {
        float radius=Math.Max(1,Math.Min(stage.Height*.35f,Math.Min(stage.Width*.3f,112)));
        double bounce=StopAt.HasValue?0:24*Math.Abs(Math.Sin(seconds*5))*Math.Exp(-seconds*1.7);
        float cx=stage.Left+stage.Width/2f,cy=stage.Top+stage.Height/2f-(float)bounce;
        PointF Project(Vector3 p)=>new(cx+p.X*radius*5/(5-p.Z),cy-p.Y*radius*5/(5-p.Z));
        var orientation=Orientation(seconds);
        var light=Vector3.Normalize(new(-.35f,.75f,1.3f));
        Color accent=ColorSafety.Selection(theme);
        bool highlighted=Highlighted(seconds);
        var visible=faces.Select(face=>
        {
            var center=Vector3.Transform(face.Center,orientation);var normal=Vector3.Transform(face.Normal,orientation);
            return new {face,center,normal,facing=Vector3.Dot(normal,Vector3.Normalize(new Vector3(0,0,5)-center))};
        }).Where(f=>f.facing>.012f).OrderBy(f=>f.center.Z);
        foreach(var item in visible)
        {
            var face=item.face;bool active=highlighted&&face.Value==Result;
            var points=face.Points.Select(p=>Project(Vector3.Transform(p,orientation))).ToArray();
            using var fill=new SolidBrush(active?Mix(theme.BackgroundColor,accent,.08f):Mix(theme.BackgroundColor,theme.TextColor,.025f+Math.Max(0,Vector3.Dot(item.normal,light))*.065f));
            using var edge=new Pen(active?accent:Mix(theme.BackgroundColor,theme.TextColor,.68f),active?1.8f:1.15f);
            graphics.FillPolygon(fill,points);graphics.DrawPolygon(edge,points);
            if(item.facing<.2f)continue;
            var center=Project(item.center);
            var u=Project(Vector3.Transform(face.Center+face.Right*.23f,orientation));
            var v=Project(Vector3.Transform(face.Center+face.Down*.23f,orientation));
            var saved=graphics.Save();
            try
            {
                using var matrix=new Matrix(u.X-center.X,u.Y-center.Y,v.X-center.X,v.Y-center.Y,center.X,center.Y);
                graphics.MultiplyTransform(matrix);
                using var label=new Font("Microsoft YaHei UI",1,FontStyle.Regular,GraphicsUnit.Pixel);
                using var ink=new SolidBrush(active?accent:theme.TextColor);
                using var format=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap};
                graphics.DrawString(face.Value.ToString(),label,ink,new RectangleF(-1.5f,-1,3,2),format);
            }
            finally {graphics.Restore(saved);}
        }
    }
}
