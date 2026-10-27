using System;
using System.IO;
using System.Security.Cryptography;

namespace MinorShift.Emuera.NativeDisplay;

internal static class EmbeddedMusic
{
    public static string Name(int track) => track switch
    {
        0 => "Celtic Impulse — Kevin MacLeod",
        1 => "Evening Fall (Harp) — Kevin MacLeod",
        _ => throw new ArgumentOutOfRangeException(nameof(track))
    };
    // Media Foundation requires a file path. Recreate the verified cache from the executable as needed.
    public static string GetPath(int track = 0)
    {
        string resourceName = track switch
        {
            0 => "EraAI.Music.CelticImpulse.mp3",
            1 => "EraAI.Music.EveningFallHarp.mp3",
            _ => throw new ArgumentOutOfRangeException(nameof(track))
        };
        using var resource = typeof(EmbeddedMusic).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Embedded music resource is missing.");
        string hash = Convert.ToHexString(SHA256.HashData(resource));
        string directory = Path.Combine(Path.GetTempPath(), "Era_AI", "Music");
        string path = Path.Combine(directory, hash + ".mp3");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            using var cached = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(cached)) == hash) return path;
        }
        resource.Position = 0;
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}
