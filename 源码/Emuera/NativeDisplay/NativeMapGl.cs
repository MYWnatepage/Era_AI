#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MinorShift.Emuera.NativeDisplay;

// A small native OpenGL renderer. No browser, JavaScript engine or continuously
// running render thread is involved; the owner renders only on invalidation.
internal sealed class NativeMapGl : IDisposable
{
    private static int liveContexts, cleanupFailures;
    internal static int LiveContexts => Volatile.Read(ref liveContexts);
    internal static int CleanupFailures => Volatile.Read(ref cleanupFailures);
    private readonly IntPtr window;
    private readonly NativeSceneWorld world;
    private IntPtr dc, context;
    private Gl? gl;
    private Mesh terrain, solids, river, routes, edges, markers, plannedRoute;
    private ProgramState? solidProgram, lineProgram;
    private int lastCurrent = int.MinValue, lastDestination = int.MinValue;
    private int lastRouteVersion = -1;
    private Vector3? lastRouteEnd;
    private bool disposed;
    internal long GeometryBytes { get; private set; }
    internal string Driver { get; private set; } = "";

    internal NativeMapGl(IntPtr windowHandle, NativeSceneWorld world)
    {
        window = windowHandle;
        this.world = world;
        try
        {
            dc = Native.GetDC(window);
            if (dc == IntPtr.Zero) throw new Win32Exception("无法建立地图绘图设备。");
            var format = new Native.PixelFormatDescriptor
            {
                Size = (ushort)Marshal.SizeOf<Native.PixelFormatDescriptor>(), Version = 1,
                Flags = 0x00000001 | 0x00000004 | 0x00000020,
                PixelType = 0, ColorBits = 32, AlphaBits = 8, DepthBits = 24, LayerType = 0
            };
            if (Native.GetPixelFormat(dc) == 0)
            {
                int chosen = Native.ChoosePixelFormat(dc, ref format);
                if (chosen == 0 || !Native.SetPixelFormat(dc, chosen, ref format))
                    throw new Win32Exception("无法选择地图像素格式。");
            }
            context = Native.wglCreateContext(dc);
            if (context != IntPtr.Zero) Interlocked.Increment(ref liveContexts);
            if (context == IntPtr.Zero || !Native.wglMakeCurrent(dc, context))
                throw new Win32Exception("无法启动原生三维地图。");
            string version = Marshal.PtrToStringAnsi(Native.glGetString(0x1F02)) ?? "";
            Driver = (Marshal.PtrToStringAnsi(Native.glGetString(0x1F01)) ?? "OpenGL") + " / " + version;
            if (version.StartsWith("1.", StringComparison.Ordinal) || version.Length == 0)
                throw new InvalidOperationException("显卡驱动不支持地图需要的 OpenGL 2.1，请更新显卡驱动。");
            gl = new Gl();
            solidProgram = MakeProgram(SolidVertex, SolidFragment);
            lineProgram = MakeProgram(LineVertex, LineFragment);
            terrain = Upload(world.Terrain, 10);
            solids = Upload(world.Solids, 10);
            river = Upload(world.River, 10);
            routes = Upload(world.Routes, 10);
            edges = Upload(world.Edges.Skip(world.TerrainEdgeFloatCount).ToArray(), 4);
            markers = Upload([], 4);
            plannedRoute = Upload([], 4);
            // Driver defaults can vary; select the same depth convention as WebGL.
            Native.glDisable(0x0B44); // CULL_FACE
            Native.glDisable(0x0BE2); // BLEND
            Native.glEnable(0x0B71); // DEPTH_TEST
            Native.glDepthFunc(0x0203); // LEQUAL
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private readonly record struct Mesh(uint Buffer, int Count, int Stride);
    private sealed class ProgramState(uint handle)
    {
        internal uint Handle { get; } = handle;
        internal Dictionary<string, int> Uniforms { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Attributes { get; } = new(StringComparer.Ordinal);
    }

    private Mesh Upload(float[] data, int stride)
    {
        var api = gl!;
        api.GenBuffers(1, out uint buffer);
        if (buffer == 0) throw new InvalidOperationException("无法建立地图模型缓冲。");
        api.BindBuffer(0x8892, buffer);
        WithPinned(data, address => api.BufferData(0x8892, new IntPtr((long)data.Length * sizeof(float)), address, 0x88E4));
        GeometryBytes += (long)data.Length * sizeof(float);
        return new Mesh(buffer, data.Length / stride, stride);
    }

    private static void WithPinned(float[] values, Action<IntPtr> action)
    {
        if (values.Length == 0) { action(IntPtr.Zero); return; }
        var pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        try { action(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    private uint MakeShader(uint type, string source)
    {
        var api = gl!;
        uint shader = api.CreateShader(type);
        if (shader == 0) throw new InvalidOperationException("无法建立地图着色器。");
        IntPtr text = Marshal.StringToHGlobalAnsi(source);
        try
        {
            api.ShaderSource(shader, 1, [text], IntPtr.Zero);
            api.CompileShader(shader);
            api.GetShaderiv(shader, 0x8B81, out int compiled);
            if (compiled == 0)
            {
                var log = new StringBuilder(4096);
                api.GetShaderInfoLog(shader, log.Capacity, out _, log);
                throw new InvalidOperationException("原生地图着色器编译失败：" + log);
            }
            return shader;
        }
        catch { api.DeleteShader(shader); throw; }
        finally { Marshal.FreeHGlobal(text); }
    }

    private ProgramState MakeProgram(string vertex, string fragment)
    {
        var api = gl!;
        uint v = MakeShader(0x8B31, vertex), f = 0, program = 0;
        try
        {
            f = MakeShader(0x8B30, fragment);
            program = api.CreateProgram();
            api.AttachShader(program, v); api.AttachShader(program, f);
            api.BindAttribLocation(program, 0, "aPosition");
            api.BindAttribLocation(program, 1, "aNormal");
            api.BindAttribLocation(program, 2, "aOwner");
            api.BindAttribLocation(program, 3, "aBary");
            api.LinkProgram(program);
            api.GetProgramiv(program, 0x8B82, out int linked);
            if (linked == 0)
            {
                var log = new StringBuilder(4096);
                api.GetProgramInfoLog(program, log.Capacity, out _, log);
                throw new InvalidOperationException("原生地图着色器连接失败：" + log);
            }
            var result = new ProgramState(program);
            foreach (string name in new[] { "uMVP", "uBG", "uFG", "uFocus", "uEye", "uActive", "uMode", "uGrid" })
                result.Uniforms[name] = api.GetUniformLocation(program, name);
            foreach (string name in new[] { "aPosition", "aNormal", "aOwner", "aBary" })
                result.Attributes[name] = api.GetAttribLocation(program, name);
            return result;
        }
        catch { if (program != 0) api.DeleteProgram(program); throw; }
        finally { api.DeleteShader(v); if (f != 0) api.DeleteShader(f); }
    }

    internal void Render(int width, int height, DisplayTheme theme, NativeMapCamera camera,
        int activeSiteId, NativeSceneSite? current, NativeSceneSite? destination, NativeMapRoute route)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (width <= 0 || height <= 0) return;
        if (!Native.wglMakeCurrent(dc, context)) throw new Win32Exception("地图绘图设备已不可用。");
        camera.Update(width, height);
        var safe = theme.Safe();
        var bg = safe.BackgroundColor;
        Native.glViewport(0, 0, width, height);
        Native.glClearColor(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1);
        Native.glClear(0x00004000 | 0x00000100);
        Native.glEnable(0x0B71); Native.glDepthFunc(0x0203);
        Native.glDisable(0x0BE2); Native.glDisable(0x0B20);
        SetUniforms(solidProgram!, safe, camera, activeSiteId);
        gl!.Uniform1f(solidProgram!.Uniforms["uGrid"], .5f);
        Native.glEnable(0x8037); // POLYGON_OFFSET_FILL
        Native.glPolygonOffset(1, 1);
        DrawMesh(terrain, 0); DrawMesh(river, 2); DrawMesh(routes, 3); DrawMesh(solids, 1);
        Native.glDisable(0x8037);
        Native.glEnable(0x0BE2); Native.glBlendFunc(0x0302, 0x0303);
        Native.glEnable(0x0B20); Native.glHint(0x0C52, 0x1102);
        SetUniforms(lineProgram!, safe, camera, activeSiteId);
        DrawLines(edges);
        if (lastCurrent != (current?.Id ?? 0) || lastDestination != (destination?.Id ?? 0) || lastRouteEnd != route.EndPoint)
            UpdateMarkers(current, destination, route.EndPoint);
        DrawLines(markers);
        if (lastRouteVersion != route.Version)
        {
            float[] data = route.BuildLineVertices();
            gl.BindBuffer(0x8892, plannedRoute.Buffer);
            WithPinned(data, address => gl.BufferData(0x8892, new IntPtr(data.Length * sizeof(float)), address, 0x88E8));
            plannedRoute = plannedRoute with { Count = data.Length / 4 }; lastRouteVersion = route.Version;
        }
        Native.glLineWidth(2);
        DrawLines(plannedRoute);
        Native.glLineWidth(1);
        Native.glDisable(0x0B20); Native.glDisable(0x0BE2);
        gl.BindBuffer(0x8892, 0);
        gl.UseProgram(0);
        if (!Native.SwapBuffers(dc)) throw new Win32Exception("地图画面无法更新。");
    }

    private void SetUniforms(ProgramState program, DisplayTheme theme, NativeMapCamera camera, int activeSiteId)
    {
        var api = gl!;
        api.UseProgram(program.Handle);
        var m = camera.ViewProjection;
        float[] matrix = [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,
            m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
        api.UniformMatrix4fv(program.Uniforms["uMVP"], 1, false, matrix);
        Colour(program, "uBG", theme.BackgroundColor); Colour(program, "uFG", theme.TextColor);
        Colour(program, "uFocus", ColorSafety.Selection(theme));
        var eye = camera.Eye; api.Uniform3f(program.Uniforms["uEye"], eye.X, eye.Y, eye.Z);
        int renderId = 0;
        foreach (var site in world.Sites) if (site.Id == activeSiteId) { renderId = site.RenderId; break; }
        api.Uniform1f(program.Uniforms["uActive"], renderId);
    }

    private void Colour(ProgramState program, string name, Color value) =>
        gl!.Uniform3f(program.Uniforms[name], value.R / 255f, value.G / 255f, value.B / 255f);

    private void Attribute(ProgramState program, string name, int size, int stride, int offset)
    {
        int location = program.Attributes[name];
        if (location < 0) return;
        gl!.EnableVertexAttribArray((uint)location);
        gl.VertexAttribPointer((uint)location, size, 0x1406, false, stride * sizeof(float), new IntPtr(offset * sizeof(float)));
    }

    private void ResetAttributes()
    {
        for (uint i = 0; i < 4; i++) gl!.DisableVertexAttribArray(i);
    }

    private void DrawMesh(Mesh mesh, float mode)
    {
        if (mesh.Count == 0) return;
        ResetAttributes();
        gl!.BindBuffer(0x8892, mesh.Buffer);
        Attribute(solidProgram!, "aPosition", 3, 10, 0); Attribute(solidProgram!, "aNormal", 3, 10, 3);
        Attribute(solidProgram!, "aOwner", 1, 10, 6); Attribute(solidProgram!, "aBary", 3, 10, 7);
        gl.Uniform1f(solidProgram!.Uniforms["uMode"], mode);
        Native.glDrawArrays(0x0004, 0, mesh.Count);
    }

    private void DrawLines(Mesh mesh)
    {
        if (mesh.Count == 0) return;
        ResetAttributes();
        gl!.BindBuffer(0x8892, mesh.Buffer);
        Attribute(lineProgram!, "aPosition", 3, 4, 0); Attribute(lineProgram!, "aOwner", 1, 4, 3);
        Native.glDrawArrays(0x0001, 0, mesh.Count);
    }

    private void UpdateMarkers(NativeSceneSite? current, NativeSceneSite? destination, Vector3? routeEnd)
    {
        var data = new List<float>(784);
        void Add(float x, float y, float z, float owner)
        {
            for (int i = 0; i < 48; i++)
            {
                float a = i * MathF.PI / 24, b = (i + 1) * MathF.PI / 24;
                data.AddRange([x + MathF.Cos(a) * 1.35f, y, z + MathF.Sin(a) * 1.35f, owner,
                    x + MathF.Cos(b) * 1.35f, y, z + MathF.Sin(b) * 1.35f, owner]);
            }
            data.AddRange([x,y,z,owner,x,y+13,z,owner]);
        }
        if (current != null) Add(current.X, world.HeightAt(current.X, current.Z) + current.MarkerHeight, current.Z, -1);
        if (routeEnd is Vector3 end) Add(end.X, end.Y + .25f, end.Z, -2);
        else if (destination != null) Add(destination.X, world.HeightAt(destination.X, destination.Z) + destination.MarkerHeight, destination.Z, -2);
        float[] array = data.ToArray();
        gl!.BindBuffer(0x8892, markers.Buffer);
        WithPinned(array, address => gl.BufferData(0x8892, new IntPtr(array.Length * sizeof(float)), address, 0x88E8));
        markers = markers with { Count = array.Length / 4 };
        lastCurrent = current?.Id ?? 0; lastDestination = destination?.Id ?? 0;
        lastRouteEnd = routeEnd;
    }

    internal Bitmap Capture(int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!Native.wglMakeCurrent(dc, context)) throw new Win32Exception("地图绘图设备已不可用。");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0,0,width,height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Native.glReadBuffer(0x0404); // FRONT: Render has swapped the completed frame.
            Native.glPixelStorei(0x0D05, 4);
            Native.glFinish();
            Native.glReadPixels(0,0,width,height,0x80E1,0x1401,bits.Scan0);
        }
        finally { Native.glReadBuffer(0x0405); bitmap.UnlockBits(bits); }
        bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
        return bitmap;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (context != IntPtr.Zero)
        {
            bool current = Native.wglMakeCurrent(dc, context);
            if (!current) Interlocked.Increment(ref cleanupFailures);
            if (current && gl != null)
            {
                // A close can follow the last SwapBuffers immediately. Complete that
                // work before releasing its buffers rather than leaving deferred GPU work.
                Native.glFinish();
                gl.UseProgram(0);
                gl.BindBuffer(0x8892, 0);
                for (uint i = 0; i < 4; i++) gl.DisableVertexAttribArray(i);
                foreach (var mesh in new[] { terrain,solids,river,routes,edges,markers,plannedRoute })
                    if (mesh.Buffer != 0) { uint id = mesh.Buffer; gl.DeleteBuffers(1, ref id); }
                if (solidProgram != null) gl.DeleteProgram(solidProgram.Handle);
                if (lineProgram != null) gl.DeleteProgram(lineProgram.Handle);
            }
            if (!Native.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero)) Interlocked.Increment(ref cleanupFailures);
            if (Native.wglDeleteContext(context)) Interlocked.Decrement(ref liveContexts);
            else Interlocked.Increment(ref cleanupFailures);
            context = IntPtr.Zero;
        }
        if (dc != IntPtr.Zero) { Native.ReleaseDC(window, dc); dc = IntPtr.Zero; }
    }

    private const string SolidVertex = """
        #version 120
        attribute vec3 aPosition;attribute vec3 aNormal;attribute float aOwner;attribute vec3 aBary;
        uniform mat4 uMVP;varying vec3 vWorld;varying vec3 vNormal;varying float vOwner;varying vec3 vBary;
        void main(){vWorld=aPosition;vNormal=aNormal;vOwner=aOwner;vBary=aBary;gl_Position=uMVP*vec4(aPosition,1.0);}
        """;
    private const string SolidFragment = """
        #version 120
        varying vec3 vWorld;varying vec3 vNormal;varying float vOwner;varying vec3 vBary;
        uniform vec3 uBG,uFG,uFocus,uEye;uniform float uActive,uMode,uGrid;
        void main(){
        vec3 n=normalize(vNormal);float diffuse=max(0.0,dot(n,normalize(vec3(.20,.88,-.42))));
        float nearFactor=1.0-smoothstep(135.0,250.0,distance(uEye,vWorld));
        float extent=max(abs(vWorld.x),abs(vWorld.z));float edgeVisibility=1.0-smoothstep(77.0+3.0*sin(vWorld.x*.047+vWorld.z*.031),108.0,extent);
        float shade=.018+.18*diffuse;vec3 colour=mix(uBG,uFG,shade);
        if(uMode<.5){vec3 fw=fwidth(vBary)*.80;vec3 sm=smoothstep(vec3(0.0),fw,vBary);float wire=1.0-min(sm.x,min(sm.y,sm.z));colour=mix(colour,mix(uBG,uFG,.43),wire*uGrid);}
        else if(uMode<1.5){colour=mix(uBG,uFG,.14+.22*diffuse);}
        else if(uMode<2.5){float wave=sin(vWorld.z*3.0+sin(vWorld.x*2.8));colour=mix(uBG,uFG,.26+.04*wave);}
        else {colour=mix(uBG,uFG,.12+.045*diffuse);}
        colour=mix(uBG,colour,(.35+.65*nearFactor)*edgeVisibility);gl_FragColor=vec4(colour,1.0);}
        """;
    private const string LineVertex = """
        #version 120
        attribute vec3 aPosition;attribute float aOwner;uniform mat4 uMVP;varying float vOwner;varying vec3 vWorld;
        void main(){vOwner=aOwner;vWorld=aPosition;gl_Position=uMVP*vec4(aPosition,1.0);}
        """;
    private const string LineFragment = """
        #version 120
        varying float vOwner;varying vec3 vWorld;uniform vec3 uBG,uFG,uFocus,uEye;uniform float uActive;
        void main(){bool active=abs(vOwner-uActive)<.1&&uActive>.5;bool present=vOwner<-.5&&vOwner>-1.5;bool target=vOwner<-1.5;
        float f=1.0-smoothstep(160.0,260.0,distance(uEye,vWorld));
        float extent=max(abs(vWorld.x),abs(vWorld.z));float edgeVisibility=1.0-smoothstep(77.0+3.0*sin(vWorld.x*.047+vWorld.z*.031),108.0,extent);
        vec3 c=present?uFG:(active||target?uFocus:mix(uBG,uFG,.60));gl_FragColor=vec4(mix(uBG,c,(.45+.55*f)*edgeVisibility),1.0);}
        """;

    private sealed class Gl
    {
        internal readonly GenBuffersProc GenBuffers = Load<GenBuffersProc>("glGenBuffers");
        internal readonly DeleteBuffersProc DeleteBuffers = Load<DeleteBuffersProc>("glDeleteBuffers");
        internal readonly BindBufferProc BindBuffer = Load<BindBufferProc>("glBindBuffer");
        internal readonly BufferDataProc BufferData = Load<BufferDataProc>("glBufferData");
        internal readonly CreateShaderProc CreateShader = Load<CreateShaderProc>("glCreateShader");
        internal readonly ShaderSourceProc ShaderSource = Load<ShaderSourceProc>("glShaderSource");
        internal readonly ShaderProc CompileShader = Load<ShaderProc>("glCompileShader");
        internal readonly GetShaderivProc GetShaderiv = Load<GetShaderivProc>("glGetShaderiv");
        internal readonly InfoLogProc GetShaderInfoLog = Load<InfoLogProc>("glGetShaderInfoLog");
        internal readonly ShaderProc DeleteShader = Load<ShaderProc>("glDeleteShader");
        internal readonly CreateProgramProc CreateProgram = Load<CreateProgramProc>("glCreateProgram");
        internal readonly AttachShaderProc AttachShader = Load<AttachShaderProc>("glAttachShader");
        internal readonly BindAttribProc BindAttribLocation = Load<BindAttribProc>("glBindAttribLocation");
        internal readonly ShaderProc LinkProgram = Load<ShaderProc>("glLinkProgram");
        internal readonly GetShaderivProc GetProgramiv = Load<GetShaderivProc>("glGetProgramiv");
        internal readonly InfoLogProc GetProgramInfoLog = Load<InfoLogProc>("glGetProgramInfoLog");
        internal readonly ShaderProc DeleteProgram = Load<ShaderProc>("glDeleteProgram");
        internal readonly ShaderProc UseProgram = Load<ShaderProc>("glUseProgram");
        internal readonly LocationProc GetUniformLocation = Load<LocationProc>("glGetUniformLocation");
        internal readonly LocationProc GetAttribLocation = Load<LocationProc>("glGetAttribLocation");
        internal readonly UniformMatrixProc UniformMatrix4fv = Load<UniformMatrixProc>("glUniformMatrix4fv");
        internal readonly Uniform1Proc Uniform1f = Load<Uniform1Proc>("glUniform1f");
        internal readonly Uniform3Proc Uniform3f = Load<Uniform3Proc>("glUniform3f");
        internal readonly AttribProc EnableVertexAttribArray = Load<AttribProc>("glEnableVertexAttribArray");
        internal readonly AttribProc DisableVertexAttribArray = Load<AttribProc>("glDisableVertexAttribArray");
        internal readonly VertexPointerProc VertexAttribPointer = Load<VertexPointerProc>("glVertexAttribPointer");
        private static T Load<T>(string name) where T : Delegate
        {
            IntPtr address = Native.wglGetProcAddress(name);
            if (address == IntPtr.Zero || address == new IntPtr(1) || address == new IntPtr(2) || address == new IntPtr(3) || address == new IntPtr(-1))
                throw new InvalidOperationException("显卡驱动缺少地图绘图功能：" + name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenBuffersProc(int count, out uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteBuffersProc(int count, ref uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindBufferProc(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BufferDataProc(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateShaderProc(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ShaderSourceProc(uint shader, int count, [In] IntPtr[] strings, IntPtr lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ShaderProc(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetShaderivProc(uint shader, uint pname, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet=CharSet.Ansi)] private delegate void InfoLogProc(uint handle, int maxLength, out int length, StringBuilder log);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateProgramProc();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttachShaderProc(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet=CharSet.Ansi)] private delegate void BindAttribProc(uint program, uint index, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet=CharSet.Ansi)] private delegate int LocationProc(uint program, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UniformMatrixProc(int location, int count, [MarshalAs(UnmanagedType.I1)] bool transpose, [In] float[] values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Uniform1Proc(int location, float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Uniform3Proc(int location, float x, float y, float z);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttribProc(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VertexPointerProc(uint index, int size, uint type, [MarshalAs(UnmanagedType.I1)] bool normalized, int stride, IntPtr pointer);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct PixelFormatDescriptor
        {
            internal ushort Size, Version;
            internal uint Flags;
            internal byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift,
                AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits,
                DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
            internal uint LayerMask, VisibleMask, DamageMask;
        }
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor format);
        [DllImport("gdi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor descriptor);
        [DllImport("gdi32.dll")] internal static extern int GetPixelFormat(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SwapBuffers(IntPtr dc);
        [DllImport("opengl32.dll", SetLastError=true)] internal static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
        [DllImport("opengl32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool wglDeleteContext(IntPtr context);
        [DllImport("opengl32.dll", CharSet=CharSet.Ansi)] internal static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] internal static extern IntPtr glGetString(uint name);
        [DllImport("opengl32.dll")] internal static extern void glViewport(int x,int y,int width,int height);
        [DllImport("opengl32.dll")] internal static extern void glClearColor(float r,float g,float b,float a);
        [DllImport("opengl32.dll")] internal static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] internal static extern void glEnable(uint feature);
        [DllImport("opengl32.dll")] internal static extern void glDisable(uint feature);
        [DllImport("opengl32.dll")] internal static extern void glDepthFunc(uint function);
        [DllImport("opengl32.dll")] internal static extern void glPolygonOffset(float factor,float units);
        [DllImport("opengl32.dll")] internal static extern void glBlendFunc(uint source,uint destination);
        [DllImport("opengl32.dll")] internal static extern void glHint(uint target,uint quality);
        [DllImport("opengl32.dll")] internal static extern void glDrawArrays(uint mode,int first,int count);
        [DllImport("opengl32.dll")] internal static extern void glLineWidth(float width);
        [DllImport("opengl32.dll")] internal static extern void glReadBuffer(uint buffer);
        [DllImport("opengl32.dll")] internal static extern void glPixelStorei(uint name,int value);
        [DllImport("opengl32.dll")] internal static extern void glFinish();
        [DllImport("opengl32.dll")] internal static extern void glReadPixels(int x,int y,int width,int height,uint format,uint type,IntPtr pixels);
    }
}
