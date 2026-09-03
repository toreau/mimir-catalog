namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral A1 analytical models. A1 is a bounded-memory stream-to-fold
/// operation: complete relation consumption plus canonical encoding and
/// MultisetFoldV1 accumulation are the measured operation; expected comparison
/// stays outside the future timer.
/// </summary>
public sealed record A1Expected(string Op, long Cardinality, string Digest);

public sealed class AnalyticalWorkload
{
    public required IReadOnlyDictionary<string, A1Expected> Expected { get; init; }
}

public sealed class A1ExecutionResult
{
    public required string Operation { get; init; }
    public required long ActualRowCount { get; init; }
    public required string ActualDigest { get; init; }
}

public sealed class A1Result
{
    public required string Operation { get; init; }
    public required string Relation { get; init; }
    public required string Status { get; init; }
    public long? ExpectedRowCount { get; init; }
    public long? ActualRowCount { get; init; }
    public string? ExpectedDigest { get; init; }
    public string? ActualDigest { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>A2-A4 grouped relational result (count is Int64 end-to-end).</summary>
public sealed class A2A4Result
{
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public long? ExpectedRowCount { get; init; }
    public long? ActualRowCount { get; init; }
    public string? ExpectedDigest { get; init; }
    public string? ActualDigest { get; init; }
    public string? ErrorMessage { get; init; }
}
