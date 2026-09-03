using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral G2 correctness: per-input and batch digest/comparison.
/// Digest computation stays outside the executor boundary so a future timer can
/// wrap only G2OperationExecutor.
/// </summary>
public sealed class G2CorrectnessRunner
{
    private readonly IStorageCandidate _candidate;

    public G2CorrectnessRunner(IStorageCandidate candidate) => _candidate = candidate;

    public IReadOnlyList<G2PerInputExecutionOutcome> Execute(IReadOnlyList<G2Concept> concepts)
        => new G2OperationExecutor(_candidate).Execute(concepts);

    public (IReadOnlyList<G2PerInputResult> PerInput, G2BatchResult Batch) RunAll(G2Workload workload)
    {
        var outcomes = Execute(workload.Concepts);
        return Classify(workload, outcomes);
    }

    public (IReadOnlyList<G2PerInputResult> PerInput, G2BatchResult Batch) Classify(
        G2Workload workload, IReadOnlyList<G2PerInputExecutionOutcome> outcomes)
    {
        var perResults = new List<G2PerInputResult>(outcomes.Count);
        bool anyError = false;
        bool anyInvalid = false;
        for (int i = 0; i < outcomes.Count; i++)
        {
            var o = outcomes[i];
            var exp = workload.PerInput[i];
            if (o.ErrorMessage != null || o.StructuralQidsAscending == null)
            {
                anyError = true;
                perResults.Add(new G2PerInputResult
                {
                    Item = o.Item, Qid = o.Qid, SourceStratum = o.SourceStratum,
                    Status = ServingStatuses.Error,
                    ExpectedCardinality = exp.Cardinality,
                    ExpectedDigest = exp.Digest,
                    ErrorMessage = o.ErrorMessage,
                });
                continue;
            }
            long card = o.StructuralQidsAscending.Length;
            string digest = WorkloadOracle.StructuralSetDigest(o.StructuralQidsAscending);
            bool valid = card == exp.Cardinality && digest == exp.Digest;
            if (!valid) anyInvalid = true;
            perResults.Add(new G2PerInputResult
            {
                Item = o.Item, Qid = o.Qid, SourceStratum = o.SourceStratum,
                Status = valid ? ServingStatuses.Valid : ServingStatuses.Invalid,
                ExpectedCardinality = exp.Cardinality,
                ActualCardinality = card,
                ExpectedDigest = exp.Digest,
                ActualDigest = digest,
            });
        }

        if (anyError)
        {
            return (perResults, new G2BatchResult
            {
                Status = ServingStatuses.Error,
                ErrorMessage = "one or more G2 per-input executions failed",
            });
        }

        var rows = outcomes.Select(o => (o.Qid, o.StructuralQidsAscending!)).ToList();
        string batchDigest = WorkloadOracle.G2BatchDigest(rows);
        bool batchValid = !anyInvalid
            && rows.Count == workload.Batch.Cardinality
            && batchDigest == workload.Batch.Digest;
        return (perResults, new G2BatchResult
        {
            Status = batchValid ? ServingStatuses.Valid : ServingStatuses.Invalid,
            ActualCardinality = rows.Count,
            ActualDigest = batchDigest,
        });
    }
}
