using SegmentScanner.Contracts;
namespace SegmentScanner.Engine.Jellyfin;

public static class NodeRegistration
{
    public static NodeHello CreateHello(WorkerConfiguration configuration, NodeRuntimeInformation runtime)
    {
        configuration.Validate();
        var server = configuration.Server ?? throw new InvalidDataException("No Jellyfin server configured.");
        ArgumentNullException.ThrowIfNull(runtime);
        return new NodeHello(server.WorkerId, server.Node.Name, runtime);
    }
}
