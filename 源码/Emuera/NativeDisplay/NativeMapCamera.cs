#nullable enable
using System;
using System.Drawing;
using System.Numerics;

namespace MinorShift.Emuera.NativeDisplay;

// Uses a row-vector .NET matrix; its row-major storage is the column-major
// transpose expected by OpenGL. The projection retains OpenGL's [-1,+1] depth.
internal sealed class NativeMapCamera
{
    internal float Yaw { get; set; } = -.43f;
    internal float Elevation { get; set; } = .82f;
    internal float Distance { get; set; } = 112f;
    internal Vector3 Target { get; set; } = new(0, 3, 1);
    internal Vector3 Eye { get; private set; }
    internal Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;
    internal Matrix4x4 InverseViewProjection { get; private set; } = Matrix4x4.Identity;

    internal void Update(int width, int height)
    {
        float aspect = Math.Max(1, width) / (float)Math.Max(1, height);
        float distance = Distance * MathF.Max(1, MathF.Sqrt(1.65f / aspect));
        float c = MathF.Cos(Elevation) * distance;
        Eye = Target + new Vector3(MathF.Sin(Yaw) * c, MathF.Sin(Elevation) * distance, MathF.Cos(Yaw) * c);
        const float near = .5f, far = 450;
        float f = 1 / MathF.Tan(.70f / 2), d = 1 / (near - far);
        var projection = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0,
            0, 0, (far + near) * d, -1, 0, 0, 2 * far * near * d, 0);
        ViewProjection = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY) * projection;
        if (!Matrix4x4.Invert(ViewProjection, out var inverse))
            throw new InvalidOperationException("地图视角无效。");
        InverseViewProjection = inverse;
    }

    internal PointF? Project(Vector3 point, int width, int height)
    {
        var p = Vector4.Transform(new Vector4(point, 1), ViewProjection);
        if (p.W <= 0 || !float.IsFinite(p.W)) return null;
        return new PointF((p.X / p.W * .5f + .5f) * width, (.5f - p.Y / p.W * .5f) * height);
    }

    internal (Vector3 Origin, Vector3 Direction) Ray(float x, float y, int width, int height)
    {
        float nx = x / Math.Max(1, width) * 2 - 1, ny = 1 - y / Math.Max(1, height) * 2;
        var a = Vector4.Transform(new Vector4(nx, ny, -1, 1), InverseViewProjection);
        var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), InverseViewProjection);
        var origin = new Vector3(a.X, a.Y, a.Z) / a.W;
        var end = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (origin, Vector3.Normalize(end - origin));
    }

    internal Vector3? FloorPoint(float x, float y, int width, int height, NativeSceneWorld world)
    {
        float nx = x / Math.Max(1, width) * 2 - 1, ny = 1 - y / Math.Max(1, height) * 2;
        var a = Vector4.Transform(new Vector4(nx, ny, -1, 1), InverseViewProjection);
        var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), InverseViewProjection);
        var p = new Vector3(a.X, a.Y, a.Z) / a.W;
        var q = new Vector3(b.X, b.Y, b.Z) / b.W;
        float previous = 0;
        for (int i = 1; i <= 140; i++)
        {
            float t = i / 140f;
            var r = Vector3.Lerp(p, q, t);
            if (r.Y <= world.HeightAt(r.X, r.Z))
            {
                float lo = previous, hi = t;
                for (int j = 0; j < 12; j++)
                {
                    float mid = (lo + hi) / 2;
                    var m = Vector3.Lerp(p, q, mid);
                    if (m.Y > world.HeightAt(m.X, m.Z)) lo = mid; else hi = mid;
                }
                var result = Vector3.Lerp(p, q, hi);
                var bounds = world.Bounds;
                if (result.X < bounds.X || result.X > bounds.Y || result.Z < bounds.Z || result.Z > bounds.W) return null;
                return result;
            }
            previous = t;
        }
        return null;
    }
}
