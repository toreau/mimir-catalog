namespace Mimir.Catalog.Benchmark;

/// <summary>
/// A2-A4 executor: dispatches to the candidate method and returns the fully
/// materialized logical grouped rows. No sorting, no digests, no expected
/// comparison, no lifecycle calls.
/// </summary>
public sealed class A2A4OperationExecutor
{
    private readonly IAnalyticalCandidate _candidate;

    public A2A4OperationExecutor(IAnalyticalCandidate candidate) => _candidate = candidate;

    public IReadOnlyList<(string Lang, string LexKind, long Count)> ExecuteA2() => _candidate.A2LangKindCounts();

    public IReadOnlyList<(long TargetQid, long Count)> ExecuteA3() => _candidate.A3P31Fanout();

    public IReadOnlyList<(long TargetQid, long Count)> ExecuteA4() => _candidate.A4P279Fanout();
}
