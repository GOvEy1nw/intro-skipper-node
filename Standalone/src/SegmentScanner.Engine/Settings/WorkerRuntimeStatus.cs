namespace SegmentScanner.Engine.Settings;

public sealed record WorkerRuntimeStatus(bool Running, bool Connected, bool Scanning, bool? NodeEnabled, string? Error, bool CanStart);
