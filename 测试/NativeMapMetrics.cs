using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// This samples the complete host process tree, so WebView's GPU and renderer
// processes are counted rather than comparing only the WinForms host process.
internal static class NativeMapMetrics
{
    private static readonly List<Snapshot> Samples = [];

    internal sealed record ProcessSample(int Pid, int ParentPid, string Name, long PrivateBytes, long WorkingSetBytes);
    internal sealed record Snapshot(string Label, DateTimeOffset At, bool ProcessTreeComplete,
        long PrivateBytes, long WorkingSetBytes, long ManagedBytes, IReadOnlyList<ProcessSample> Processes);

    internal static Snapshot Capture(string label)
    {
        int own = Environment.ProcessId;
        var parents = ReadParents(out bool complete);
        var included = new HashSet<int> { own };
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in parents)
                if (included.Contains(pair.Value) && included.Add(pair.Key)) changed = true;
        } while (changed);

        var processes = new List<ProcessSample>();
        foreach (int id in included.Order())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Refresh();
                processes.Add(new(id, parents.GetValueOrDefault(id), process.ProcessName,
                    process.PrivateMemorySize64, process.WorkingSet64));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Child processes can exit between enumeration and measurement.
                if (id == own) complete = false;
            }
        }
        return new(label, DateTimeOffset.UtcNow, complete, processes.Sum(p => p.PrivateBytes),
            processes.Sum(p => p.WorkingSetBytes), GC.GetTotalMemory(false), processes);
    }

    internal static void Write(string label)
    {
        var sample = Capture(label);
        Samples.Add(sample);
        Console.WriteLine($"METRIC map {label}: private={sample.PrivateBytes} working={sample.WorkingSetBytes} processes={sample.Processes.Count}");
        string? path = Environment.GetEnvironmentVariable("ERA_MAP_METRICS");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Samples, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }));
    }

    private static Dictionary<int, int> ReadParents(out bool complete)
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        complete = snapshot != new IntPtr(-1);
        if (!complete) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) { complete = false; return result; }
            do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
