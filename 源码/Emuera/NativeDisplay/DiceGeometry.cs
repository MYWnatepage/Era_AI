#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MinorShift.Emuera.NativeDisplay;

// Shared geometry only. The player exposes no die picker or script-side customization API.
internal sealed record DiceFace(Vector3[] Points, Vector3 Center, Vector3 Normal, int Value, Quaternion Target, Vector3 Right, Vector3 Down);
internal static class DiceGeometry
{
    private const float Epsilon = .00001f;
    private sealed record Plane(Vector3[] Points, Vector3 Center, Vector3 Normal, float Distance);
    internal static readonly IReadOnlyDictionary<int, DiceFace[]> Models = Build();

    private static Dictionary<int, DiceFace[]> Build()
    {
        float p = (1 + MathF.Sqrt(5)) / 2;
        Vector3[] ico = [new(-1,p,0),new(1,p,0),new(-1,-p,0),new(1,-p,0),new(0,-1,p),new(0,1,p),new(0,-1,-p),new(0,1,-p),new(p,0,-1),new(p,0,1),new(-p,0,-1),new(-p,0,1)];
        ico = ico.Select(Vector3.Normalize).ToArray();
        var antiprism = Enumerable.Range(0,10).Select(i =>
        {
            float angle = i % 5 * 2 * MathF.PI / 5 + (i < 5 ? 0 : MathF.PI / 5);
            return new Vector3(MathF.Cos(angle), MathF.Sin(angle), i < 5 ? .5f : -.5f);
        }).ToArray();
        var vertices = new Dictionary<int, Vector3[]>
        {
            [4] = new Vector3[] {new(1,1,1),new(1,-1,-1),new(-1,1,-1),new(-1,-1,1)}.Select(Vector3.Normalize).ToArray(),
            [6] = Enumerable.Range(0,8).Select(i=>Vector3.Normalize(new Vector3((i&1)>0?1:-1,(i&2)>0?1:-1,(i&4)>0?1:-1))).ToArray(),
            [8] = [Vector3.UnitX,-Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ],
            [10] = Dual(antiprism), [12] = Dual(ico), [20] = ico
        };
        return vertices.ToDictionary(pair=>pair.Key,pair=>NumberFaces(Hull(pair.Value),pair.Key));
    }
    private static Vector3[] Dual(Vector3[] vertices)
    {
        var points = Hull(vertices).Select(f=>f.Normal/f.Distance).ToArray();
        float radius = points.Max(v=>v.Length());
        return points.Select(v=>v/radius).ToArray();
    }
    private static Plane[] Hull(Vector3[] vertices)
    {
        var planes = new Dictionary<string,Plane>();
        for (int a=0;a<vertices.Length-2;a++) for (int b=a+1;b<vertices.Length-1;b++) for (int c=b+1;c<vertices.Length;c++)
        {
            var normal = Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
            if (normal.Length()<Epsilon) continue;
            normal=Vector3.Normalize(normal); float distance=Vector3.Dot(normal,vertices[a]);
            var offsets=vertices.Select(v=>Vector3.Dot(normal,v)-distance).ToArray();
            if (offsets.Any(v=>v>Epsilon) && offsets.Any(v=>v < -Epsilon)) continue;
            if (offsets.Any(v=>v>Epsilon)) {normal=-normal;distance=-distance;}
            var indices=Enumerable.Range(0,vertices.Length).Where(i=>MathF.Abs(Vector3.Dot(normal,vertices[i])-distance)<Epsilon).ToArray();
            string key=string.Join(",",indices); if (planes.ContainsKey(key)) continue;
            var center=indices.Select(i=>vertices[i]).Aggregate(Vector3.Zero,(sum,v)=>sum+v)/indices.Length;
            var u=Vector3.Normalize(vertices[indices[0]]-center); var v=Vector3.Cross(normal,u);
            var points=indices.OrderBy(i=>MathF.Atan2(Vector3.Dot(vertices[i]-center,v),Vector3.Dot(vertices[i]-center,u))).Select(i=>vertices[i]).ToArray();
            planes.Add(key,new(points,center,normal,distance));
        }
        return planes.Values.ToArray();
    }
    private static DiceFace[] NumberFaces(Plane[] faces,int sides)
    {
        if (faces.Length!=sides) throw new InvalidOperationException("骰子几何面数错误。");
        int[] values=new int[sides]; int next=1;
        for (int i=0;i<sides;i++)
        {
            if (values[i]!=0) continue;
            values[i]=next;
            if (sides!=4)
            {
                int opposite=Enumerable.Range(0,sides).Where(j=>values[j]==0).MinBy(j=>Vector3.Dot(faces[i].Normal,faces[j].Normal));
                values[opposite]=sides+1-next;
            }
            next++;
        }
        return faces.Select((face,i)=>
        {
            var n=face.Normal;
            var align=Vector3.Dot(n,Vector3.UnitZ)<-.99999f?Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI):Quaternion.Normalize(new Quaternion(Vector3.Cross(n,Vector3.UnitZ),1+Vector3.Dot(n,Vector3.UnitZ)));
            var top=face.Points.Aggregate((best,v)=>(v-face.Center).LengthSquared()>(best-face.Center).LengthSquared()+Epsilon?v:best);
            var upright=Vector3.Transform(top-face.Center,align);
            var target=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2-MathF.Atan2(upright.Y,upright.X))*align);
            if (sides==4) target=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY,-.68f)*Quaternion.CreateFromAxisAngle(Vector3.UnitX,.16f)*target);
            if (sides==6) target=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY,-.38f)*Quaternion.CreateFromAxisAngle(Vector3.UnitX,.3f)*Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/4)*target);
            var right=sides==6?Vector3.Normalize(face.Points[1]-face.Points[0]):Vector3.Normalize(Vector3.Cross(top-face.Center,n));
            if (Vector3.Transform(right,target).X<0) right=-right;
            return new DiceFace(face.Points,face.Center,n,values[i],target,right,-Vector3.Cross(n,right));
        }).ToArray();
    }
}
