namespace Mimir.Catalog.Benchmark;

public enum G1SummaryStatus
{
    Valid,
    Incomplete,
}

public enum G1IncompleteReason
{
    ExecutionEvidenceInvalid,
    StableArtifactCaptureFailed,
    ProcessIncomplete,
    EnvelopeNotValid,
    MeasuredSequenceIncomplete,
    MissingSamples,
    InvalidSample,
    TimeoutSample,
    ErrorSample,
    NotAttemptedDueToHalt,
}

public sealed record G1SummaryMetrics(
    long Count,
    double MinSeconds,
    double P50Seconds,
    double P90Seconds,
    double P95Seconds,
    double P99Seconds,
    double MaxSeconds,
    double MeanSeconds,
    double ThroughputPerSecond);

/// <summary>
/// Deterministic one-child facts consumed by the G1 repetition-summary
/// calculator. Mirrors the closed one-child result axes without CLI types.
/// </summary>
public sealed record G1ChildSnapshot(
    bool EvidenceValid,
    bool RegisteredStableArtifacts,
    bool ProcessCompleted,
    string EnvelopeStatus,
    bool MeasuredSequenceComplete,
    IReadOnlyList<G1ParentSample> ParentSamples);

public sealed record G1RepetitionSummary(
    string Operation,
    string Stratum,
    int Repetition,
    G1SummaryStatus Status,
    IReadOnlyList<G1IncompleteReason> Reasons,
    long ExpectedCount,
    long ObservedCount,
    long ValidCount,
    long InvalidCount,
    long TimeoutCount,
    long ErrorCount,
    G1SummaryMetrics? Metrics);
