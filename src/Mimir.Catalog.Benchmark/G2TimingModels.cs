namespace Mimir.Catalog.Benchmark;

public sealed record G2TimedPerInputResult(
    int Item,
    long Qid,
    string SourceStratum,
    string CorrectnessStatus,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

public sealed record G2TimedBatchResult(
    double WallSeconds,
    string CorrectnessStatus,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Result of one G2 child execution.</summary>
public sealed class G2TimingExecution
{
    public const string Operation = "G2";
    public const long Sequence = 500;
    public required int Repetition { get; init; }
    /// <summary>VALID / INVALID / ERROR over the warmup + timed batch.</summary>
    public required string Correctness { get; init; }
    public required IReadOnlyList<G2TimedPerInputResult> PerInputResults { get; init; }
    /// <summary>Present only when a timed batch ran.</summary>
    public G2TimedBatchResult? BatchResult { get; init; }
    /// <summary>warmup | timed-batch | runtime.</summary>
    public string? ErrorCategory { get; init; }
    public string? ErrorMessage { get; init; }
}
