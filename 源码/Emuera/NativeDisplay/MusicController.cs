using System;
using System.IO;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.NativeDisplay;

// Reuses the ERB BGM channel so scripts and settings do not start competing players.
internal sealed class MusicController : IDisposable
{
    private readonly Sound player;
    private readonly MusicSettingsStore store;
    private readonly string root;
    private bool started;
    public MusicSettings Settings { get; private set; }
    public string Status { get; private set; } = "";
    public string TrackName => Settings.CustomPath.Length == 0 ? EmbeddedMusic.Name(Settings.BuiltInTrack) : Path.GetFileName(Settings.CustomPath);
    public string BuiltInPath => EmbeddedMusic.GetPath(Settings.BuiltInTrack);
    public MusicController(string root, Sound player)
    {
        this.root = root;
        this.player = player;
        store = new(Path.Combine(root, "配置", "music-settings.json"));
        Settings = store.Load();
    }
    public void Ensure()
    {
        if (started) return;
        started = true;
        Play();
    }
    private void Play()
    {
        Status = "";
        player.stop();
        player.setVolume(Settings.Volume);
        if (!Settings.Enabled) return;
        try
        {
            var path = Settings.CustomPath;
            if (path.Length == 0) path = BuiltInPath;
            else if (!File.Exists(path))
            {
                path = BuiltInPath;
                Status = "自定义文件已丢失，暂用内置音乐；请重新选择文件。";
            }
            player.play(path, -1);
        }
        catch (Exception)
        {
            player.stop();
            Status = "音乐暂时无法播放，请检查音乐文件及声音设备。";
        }
    }
    public string Toggle()
    {
        var next = Settings with { Enabled = !Settings.Enabled };
        if (!store.Save(next)) return "保存失败，请检查游戏目录的写入权限。";
        Settings = next;
        Play();
        return "";
    }
    public string ToggleFromButton()
    {
        bool audible = Settings.Enabled && Settings.Volume > 0;
        var next = Settings with { Enabled = !audible, Volume = !audible && Settings.Volume == 0 ? 50 : Settings.Volume };
        if (!store.Save(next)) return "保存失败，请检查游戏目录的写入权限。";
        Settings = next;
        Play();
        return "";
    }
    public string SetVolume(int value, bool save = true)
    {
        if (value is < 0 or > 100) return "请输入 0～100 的整数。";
        var next = Settings with { Volume = value };
        if (save && !store.Save(next)) return "音量保存失败，请检查游戏目录的写入权限。";
        Settings = next;
        player.setVolume(value);
        return "";
    }
    public string SelectBuiltIn(int track)
    {
        if (track is < 0 or > 1) return "请选择列表中的内置曲目。";
        var next = Settings with { BuiltInTrack = track, CustomPath = "" };
        if (!store.Save(next)) return "保存失败，请检查游戏目录的写入权限。";
        Settings = next;
        Play();
        return "";
    }
    public string SelectTrack(string input)
    {
        var path = input.Trim().Trim('"');
        if (path.Length > 0)
        {
            try
            {
                path = Path.GetFullPath(path, root);
                if (!File.Exists(path)) return "文件不存在，请输入本地音乐文件的完整路径。";
                if (Path.GetExtension(path).ToLowerInvariant() is not (".mp3" or ".wav" or ".ogg"))
                    return "支持 MP3、WAV、OGG 文件。";
                // Validate even while music is disabled. A broken file must not replace the last usable choice.
                Sound.ValidateFile(path);
            }
            catch (Exception) { return "无法读取此音乐，请检查文件格式、路径或声音设备。"; }
        }
        var next = Settings with { CustomPath = path };
        if (!store.Save(next)) return "保存失败，请检查游戏目录的写入权限。";
        Settings = next;
        Play();
        return "";
    }
    public void Dispose() => player.close();
}
