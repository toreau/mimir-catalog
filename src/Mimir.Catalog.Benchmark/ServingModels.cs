namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral serving workload model: published S1-S5 probes and their
/// expected correctness rows. Identity is (op, seq); measured vs
/// correctness-only is preserved for later timing exclusion.
/// </summary>
public sealed record ServingProbe(string Op, long Seq, string Stratum, bool Measured, long? Qid, string? Lang, string? Value);

public sealed record ServingExpected(string Op, long Seq, bool Measured, long Cardinality, string Digest);

public sealed class ServingWorkload
{
    public required IReadOnlyList<ServingProbe> Probes { get; init; }
    public required IReadOnlyDictionary<(string Op, long Seq), ServingExpected> Expected { get; init; }
}

public sealed class ProbeResult
{
    public required string Op { get; init; }
    public required long Seq { get; init; }
    public required string Stratum { get; init; }
    public required bool Measured { get; init; }
    public required string Status { get; init; } // VALID / INVALID / ERROR
    public long? ExpectedCardinality { get; init; }
    public long? ActualCardinality { get; init; }
    public string? ExpectedDigest { get; init; }
    public string? ActualDigest { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Candidate-neutral serving probe/expected executor boundary.</summary>
public static class ServingStatuses
{
    public const string Valid = "VALID";
    public const string Invalid = "INVALID";
    public const string Error = "ERROR";
}
