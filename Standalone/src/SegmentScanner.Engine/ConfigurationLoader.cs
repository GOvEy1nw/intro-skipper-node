using SegmentScanner.Contracts;

namespace SegmentScanner.Engine;

public static class ConfigurationLoader
{
    public static WorkerConfiguration Load(string path)
    {
        path = Path.GetFullPath(path);
        var configuration = WireJson.Read<WorkerConfiguration>(File.ReadAllText(path));
        var directory = Path.GetDirectoryName(path)!;
        string Resolve(string value) => Path.GetFullPath(value, directory);
        string Binary(string value) => value.IndexOfAny(['/', '\\']) >= 0 ? Resolve(value) : value;
        configuration = configuration with
        {
            Paths = configuration.Paths with
            {
                StateDirectory = Resolve(configuration.Paths.StateDirectory),
                CacheDirectory = Resolve(configuration.Paths.CacheDirectory),
                Ffmpeg = Binary(configuration.Paths.Ffmpeg),
                Ffprobe = Binary(configuration.Paths.Ffprobe),
                Python = Binary(configuration.Paths.Python),
                InferenceScript = Resolve(configuration.Paths.InferenceScript),
                AllowedMediaRoots = configuration.Paths.AllowedMediaRoots.Select(Resolve).ToList(),
                Mappings = configuration.Paths.Mappings.Select(m => m with { WorkerPrefix = Resolve(m.WorkerPrefix) }).ToList(),
            },
            Credits = configuration.Credits with { ModelManifest = configuration.Credits.ModelManifest is { } model ? Resolve(model) : null },
            Exports = configuration.Exports with { MirroredRoot = configuration.Exports.MirroredRoot is { } export ? Resolve(export) : null },
        };
        configuration.Validate();
        ContractValidation.Require(!configuration.Paths.StateDirectory.StartsWith("\\\\", StringComparison.Ordinal)
            && !configuration.Paths.CacheDirectory.StartsWith("\\\\", StringComparison.Ordinal), "Worker state/cache must be local, not a UNC database share.");
        return configuration;
    }
}
