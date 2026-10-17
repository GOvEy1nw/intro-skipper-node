using SegmentScanner.Contracts;
using SegmentScanner.Engine;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Desktop;

internal static class DesktopConfiguration
{
    public static WorkerConfiguration Prepare(string path)
    {
        PathMapper.RejectLinks(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var payload = Path.Combine(AppContext.BaseDirectory, "runtime");
        string Asset(string relative)
        {
            var asset = Path.Combine(payload, relative);
            if (!File.Exists(asset)) throw new InvalidDataException("The bundled scanner tools are incomplete. Run the complete desktop EXE again.");
            return asset;
        }
        if (!File.Exists(path))
        {
            var seedPath = Path.Combine(payload, "seed.json");
            var seed = File.Exists(seedPath) ? WireJson.Read<WorkerConfiguration>(File.ReadAllText(seedPath)) : new WorkerConfiguration
            {
                Paths = new() { StateDirectory = Path.Combine(directory, "state"), CacheDirectory = Path.Combine(directory, "cache") },
            };
            AtomicFile.Write(path, WireJson.Write(seed));
        }
        var configuration = ConfigurationLoader.Load(path);
        return configuration with
        {
            Paths = configuration.Paths with
            {
                Ffmpeg = Asset(Path.Combine("ffmpeg", "ffmpeg.exe")),
                Ffprobe = Asset(Path.Combine("ffmpeg", "ffprobe.exe")),
                Python = Asset(Path.Combine("python", "python.exe")),
                InferenceScript = Asset(Path.Combine("inference", "worker.py")),
            },
            Credits = configuration.Credits with { ModelManifest = Asset(Path.Combine("models", "manifest.json")) },
        };
    }
}
