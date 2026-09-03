namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral G1 workload and correctness models. All graph semantics,
/// determinism and correctness live in the harness; storage only supplies
/// P279 adjacency through IStorageCandidate.GetSubclassOf.
/// </summary>
public sealed record GraphProbe(string Op, long Seq, string Stratum, bool Measured, long StartQid);

public sealed record GraphExpected(string Op, long Seq, bool Measured, long Cardinality, long Visited, string Digest);

public sealed class GraphWorkload
{
    public required IReadOnlyList<GraphProbe> Probes { get; init; }
    public required IReadOnlyDictionary<(string Op, long Seq), GraphExpected> Expected { get; init; }
}

public sealed class G1Result
{
    public required string Op { get; init; }
    public required long Seq { get; init; }
    public required string Stratum { get; init; }
    public required bool Measured { get; init; }
    public required string Status { get; init; }
    public long? ExpectedCardinality { get; init; }
    public long? ActualCardinality { get; init; }
    public long? ExpectedVisited { get; init; }
    public long? ActualVisited { get; init; }
    public string? ExpectedDigest { get; init; }
    public string? ActualDigest { get; init; }
    public string? ErrorMessage { get; init; }
}
