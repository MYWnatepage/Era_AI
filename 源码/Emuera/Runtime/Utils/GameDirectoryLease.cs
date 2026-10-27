using System;
using System.IO;

namespace MinorShift.Emuera.Runtime.Utils;

// Saves, configuration and active AI memory share a game folder. Keep one writer
// for that entire folder; independent copies may still run at the same time.
internal sealed class GameDirectoryLease : IDisposable
{
    private readonly FileStream stream;
    private GameDirectoryLease(FileStream stream) { this.stream = stream; }
    internal static GameDirectoryLease Acquire(string gameDirectory)
    {
        string path = Path.Combine(Path.GetFullPath(gameDirectory), ".era-ai-session.lock");
        try
        {
            return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        { throw new InvalidOperationException("这个游戏文件夹已经有一个窗口在运行。请切换到已有窗口；如需同时游玩不同进度，请使用独立的游戏文件夹。", e); }
    }
    public void Dispose() { stream.Dispose(); }
}
