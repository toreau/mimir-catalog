namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral A1 correctness runner. Executes each of the four A1
/// stream-to-fold operations and compares row count + digest. Never owns
/// candidate lifecycle.
/// </summary>
public sealed class A1CorrectnessRunner
{
    private readonly IAnalyticalCandidate _candidate;

    public A1CorrectnessRunner(IAnalyticalCandidate candidate) => _candidate = candidate;

    public IReadOnlyList<A1Result> RunAll(AnalyticalWorkload workload)
    {
        var results = new List<A1Result>(4);
        foreach (var op in new[] { "A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf" })
            results.Add(RunOperation(op, workload.Expected[op]));
        return results;
    }

    public A1Result RunOperation(string operation, A1Expected expected)
    {
        try
        {
            var exec = new A1OperationExecutor(_candidate).Execute(operation);
            bool valid = exec.ActualRowCount == expected.Cardinality && exec.ActualDigest == expected.Digest;
            return new A1Result
            {
                Operation = operation,
                Relation = operation[3..],
                Status = valid ? ServingStatuses.Valid : ServingStatuses.Invalid,
                ExpectedRowCount = expected.Cardinality,
                ActualRowCount = exec.ActualRowCount,
                ExpectedDigest = expected.Digest,
                ActualDigest = exec.ActualDigest,
            };
        }
        catch (Exception ex)
        {
            return new A1Result
            {
                Operation = operation,
                Relation = operation[3..],
                Status = ServingStatuses.Error,
                ExpectedRowCount = expected.Cardinality,
                ExpectedDigest = expected.Digest,
                ErrorMessage = ex.Message,
            };
        }
    }
}
