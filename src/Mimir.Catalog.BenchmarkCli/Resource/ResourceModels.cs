namespace Mimir.Catalog.BenchmarkCli.Resource;

/// <summary>Closed resource-measurement axis over a frozen process result.</summary>
public enum ResourceMeasurementStatus
{
    Valid,
    Unavailable,
    Error,
}

/// <summary>
/// Orthogonal result: process/protocol outcome (ProcessResult) plus resource
/// measurement validity. A resource error never mutates the child logical
/// status or ProcessOutcome.
/// </summary>
public sealed class ResourceMeasuredProcessResult
{
    public required Process.ChildProcessResult ProcessResult { get; init; }
    public required ResourceMeasurementStatus ResourceStatus { get; init; }
    public long? ExternalPeakRssBytes { get; init; }
    public string? ResourceError { get; init; }
    public required string ResourceOutputPath { get; init; }
}
