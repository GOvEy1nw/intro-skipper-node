using SegmentScanner.Contracts;

namespace SegmentScanner.Engine.Media;

public sealed class PathMapper(WorkerPaths paths)
{
    public string Resolve(string source, string? mapId = null)
    {
        if (source.Contains("://", StringComparison.Ordinal) || source.Split(['/', '\\']).Any(c => c is "." or ".."))
            throw new InvalidDataException("Media paths must be local directory paths without traversal.");
        var normalized = source.Replace('\\', '/');
        var mapping = paths.Mappings.Where(m => mapId is null || m.Id == mapId)
            .Where(m => NodeScope.MatchesPath(m.ServerPrefix, m.CaseSensitive, normalized))
            .OrderByDescending(m => m.ServerPrefix.Length).FirstOrDefault();
        string resolved;
        if (mapping is not null)
        {
            var relative = normalized[mapping.ServerPrefix.Replace('\\', '/').TrimEnd('/').Length..].TrimStart('/');
            resolved = Path.GetFullPath(Path.Combine(mapping.WorkerPrefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureWithin(resolved, mapping.WorkerPrefix);
        }
        else
        {
            if (mapId is not null || !Path.IsPathFullyQualified(source)) throw new InvalidDataException("Unmapped media path.");
            resolved = Path.GetFullPath(source);
            if (!paths.AllowedMediaRoots.Any(r => IsWithin(resolved, r))) throw new InvalidDataException("Media path is outside allowed roots.");
        }

        RejectLinks(resolved);
        return resolved;
    }

    public static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    public static void EnsureWithin(string path, string root)
    {
        if (!IsWithin(path, root)) throw new InvalidDataException("Path escapes the allowed root.");
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Version 1 rejects symbolic links and reparse points; use the real allowed path.");
        }
    }

}
