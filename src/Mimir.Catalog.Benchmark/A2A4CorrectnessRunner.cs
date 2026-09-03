using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral A2-A4 correctness: canonical ordering, canonical encoding
/// and digest comparison all happen outside the executor boundary. Executor
/// invocations run inside each operation's try so one ERROR does not prevent
/// later operations.
/// </summary>
public sealed class A2A4CorrectnessRunner
{
    private readonly IAnalyticalCandidate _candidate;

    public A2A4CorrectnessRunner(IAnalyticalCandidate candidate) => _candidate = candidate;

    public IReadOnlyList<A2A4Result> RunAll(AnalyticalWorkload workload)
    {
        var executor = new A2A4OperationExecutor(_candidate);
        var results = new List<A2A4Result>(3);
        results.Add(RunOperation("A2", workload.Expected["A2"], () => ClassifyA2(executor.ExecuteA2())));
        results.Add(RunOperation("A3", workload.Expected["A3"], () => ClassifyTargets(executor.ExecuteA3())));
        results.Add(RunOperation("A4", workload.Expected["A4"], () => ClassifyTargets(executor.ExecuteA4())));
        return results;
    }

    public A2A4Result RunOperation(string op, A1Expected expected, Func<(long Count, string Digest)> canonical)
    {
        try
        {
            var (count, digest) = canonical();
            bool valid = count == expected.Cardinality && digest == expected.Digest;
            return new A2A4Result
            {
                Operation = op,
                Status = valid ? ServingStatuses.Valid : ServingStatuses.Invalid,
                ExpectedRowCount = expected.Cardinality,
                ActualRowCount = count,
                ExpectedDigest = expected.Digest,
                ActualDigest = digest,
            };
        }
        catch (Exception ex)
        {
            return new A2A4Result
            {
                Operation = op,
                Status = ServingStatuses.Error,
                ExpectedRowCount = expected.Cardinality,
                ExpectedDigest = expected.Digest,
                ErrorMessage = ex.Message,
            };
        }
    }

    private static (long Count, string Digest) ClassifyA2(IReadOnlyList<(string Lang, string LexKind, long Count)> rows)
    {
        var sorted = rows
            .OrderBy(r => r.Lang, StringComparer.Ordinal)
            .ThenBy(r => r.LexKind, StringComparer.Ordinal)
            .Select(r => WorkloadOracle.LangKindCountRow(r.Lang, r.LexKind, r.Count))
            .ToArray();
        return (rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted));
    }

    private static (long Count, string Digest) ClassifyTargets(IReadOnlyList<(long TargetQid, long Count)> rows)
    {
        var sorted = rows
            .OrderBy(r => r.TargetQid)
            .Select(r => WorkloadOracle.TargetCountRow(r.TargetQid, r.Count))
            .ToArray();
        return (rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted));
    }
}
