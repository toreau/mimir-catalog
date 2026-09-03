using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral A5 correctness: full materialized candidate result is
/// canonicalized (TargetQid ascending), encoded via WorkloadOracle.A5Row and
/// digested with AnalyticalRowsDigest. A future timer wraps the candidate
/// method; canonicalization/comparison stay outside.
/// </summary>
public sealed class A5CorrectnessRunner
{
    private readonly IAnalyticalCandidate _candidate;

    public A5CorrectnessRunner(IAnalyticalCandidate candidate) => _candidate = candidate;

    public A2A4Result Run(AnalyticalWorkload workload) => RunOperation(workload.Expected["A5"]);

    public A2A4Result RunOperation(A1Expected expected)
    {
        try
        {
            var rows = _candidate.A5P31TargetLabels();
            var sorted = rows
                .OrderBy(r => r.TargetQid)
                .Select(r => WorkloadOracle.A5Row(r.TargetQid, r.Fanout, r.EnLabel, r.NbLabel))
                .ToArray();
            long count = rows.Count;
            string digest = WorkloadOracle.AnalyticalRowsDigest(sorted);
            bool valid = count == expected.Cardinality && digest == expected.Digest;
            return new A2A4Result
            {
                Operation = "A5",
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
                Operation = "A5",
                Status = ServingStatuses.Error,
                ExpectedRowCount = expected.Cardinality,
                ExpectedDigest = expected.Digest,
                ErrorMessage = ex.Message,
            };
        }
    }
}
