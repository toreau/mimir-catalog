namespace Mimir.Catalog.Benchmark;

/// <summary>One raw timed G1 start sample. Never carries TIMEOUT.</summary>
public sealed record G1TimedSample(
    string Operation,
    long Sequence,
    string Stratum,
    double WallSeconds,
    string CorrectnessStatus,
    long? ActualCardinality = null,
    long? ActualVisited = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Result of one G1 child execution.</summary>
public sealed class G1TimingExecution
{
    public const string Operation = "G1";
    public required int Repetition { get; init; }
    /// <summary>VALID / INVALID / ERROR over the complete warmup+timed pass.</summary>
    public required string Correctness { get; init; }
    public required IReadOnlyList<G1TimedSample> Samples { get; init; }
    /// <summary>Diagnostic wall around the complete timed-pass loop; not a per-start authority.</summary>
    public double? TimedPassWallSeconds { get; init; }
    /// <summary>warmup | timed-start | runtime.</summary>
    public string? ErrorCategory { get; init; }
    public string? ErrorMessage { get; init; }
}
