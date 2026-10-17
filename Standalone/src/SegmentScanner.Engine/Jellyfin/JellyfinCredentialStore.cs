using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Media;
using SegmentScanner.Engine.Persistence;

namespace SegmentScanner.Engine.Jellyfin;

internal static class JellyfinCredentialStore
{
    private const string StoreName = ".segmentscanner-credentials";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string Origin(ServerConfiguration server) => new Uri(server.BaseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Path).TrimEnd('/');

    public static string? Load(WorkerConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows() || configuration.Server is not { } server) return null;
        var path = CredentialPath(configuration, server);
        if (!File.Exists(path)) return null;
        PathMapper.RejectLinks(path);
        try { return Protector(configuration, server).Unprotect(File.ReadAllText(path)); }
        catch (CryptographicException exception) { throw new InvalidDataException("The remembered Jellyfin credential cannot be read. Remove it and connect again.", exception); }
    }

    public static void Save(WorkerConfiguration configuration, string credential)
    {
        if (!OperatingSystem.IsWindows() || configuration.Server is not { } server) return;
        var path = CredentialPath(configuration, server);
        PathMapper.RejectLinks(path);
        AtomicFile.Write(path, Protector(configuration, server).Protect(credential));
    }

    public static bool Exists(WorkerConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows() || configuration.Server is not { } server) return false;
        var path = CredentialPath(configuration, server);
        PathMapper.RejectLinks(path);
        return File.Exists(path);
    }

    public static void Remove(WorkerConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows() || configuration.Server is not { } server) return;
        var path = CredentialPath(configuration, server);
        PathMapper.RejectLinks(path);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string CredentialPath(WorkerConfiguration configuration, ServerConfiguration server)
    {
        var directory = Path.Combine(configuration.Paths.StateDirectory, StoreName);
        PathMapper.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Origin(server))));
        return Path.Combine(directory, name + ".credential");
    }

    [SupportedOSPlatform("windows")]
    private static IDataProtector Protector(WorkerConfiguration configuration, ServerConfiguration server)
    {
        var directory = Path.Combine(configuration.Paths.StateDirectory, StoreName, "keys");
        PathMapper.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        return DataProtectionProvider.Create(new DirectoryInfo(directory), builder => builder
            .SetApplicationName("SegmentScanner")
            .ProtectKeysWithDpapi())
            .CreateProtector("SegmentScanner", "JellyfinCredential", Origin(server));
    }
}
