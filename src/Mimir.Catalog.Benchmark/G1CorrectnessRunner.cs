using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral G1 correctness runner over P279 adjacency. GraphTraversal
/// is reused directly; the storage candidate only provides fully materialized
/// parent adjacency. The runner never calls Open(); caller/orchestration owns
/// candidate lifecycle. Future per-start timing wraps the whole Traverse call
/// (including adjacency sort inside the parents callback); digest/comparison
/// stay outside that timer.
/// </summary>
public sealed class G1CorrectnessRunner
{
    public const int MaxDepth = 3;
    public const long Guard = 5000;

    private readonly IStorageCandidate _candidate;

    public G1CorrectnessRunner(IStorageCandidate candidate) => _candidate = candidate;

    /// <summary>
    /// Executes the frozen BFS for one start. The parents callback materializes
    /// GetSubclassOf and returns an ascending copy, all inside the traversal
    /// boundary (so a future timer around this call includes it).
    /// </summary>
    public GraphTraversal.Result Traverse(GraphProbe probe)
    {
        return GraphTraversal.Ancestry(
            probe.StartQid,
            maxDepth: MaxDepth,
            guard: Guard,
            parents: qid =>
            {
                var parents = _candidate.GetSubclassOf(qid);
                return parents.OrderBy(x => x).ToArray();
            });
    }

    public IReadOnlyList<G1Result> RunAll(GraphWorkload workload)
    {
        var results = new List<G1Result>(workload.Probes.Count);
        foreach (var probe in workload.Probes)
            results.Add(RunProbe(probe, workload.Expected[("G1", probe.Seq)]));
        return results;
    }

    public G1Result RunProbe(GraphProbe probe, GraphExpected expected)
    {
        try
        {
            var traversal = Traverse(probe);
            if (traversal.ExceededGuard)
            {
                return new G1Result
                {
                    Op = "G1",
                    Seq = probe.Seq,
                    Stratum = probe.Stratum,
                    Measured = probe.Measured,
                    Status = ServingStatuses.Error,
                    ExpectedCardinality = expected.Cardinality,
                    ExpectedVisited = expected.Visited,
                    ActualCardinality = traversal.Discovered.Length,
                    ActualVisited = traversal.VisitedCount,
                    ErrorMessage = "guard exceeded (5000) during G1 execution",
                };
            }

            string digest = WorkloadOracle.G1Digest(traversal.Discovered, traversal.VisitedCount);
            bool valid = traversal.Discovered.Length == expected.Cardinality
                && traversal.VisitedCount == expected.Visited
                && digest == expected.Digest;
            return new G1Result
            {
                Op = "G1",
                Seq = probe.Seq,
                Stratum = probe.Stratum,
                Measured = probe.Measured,
                Status = valid ? ServingStatuses.Valid : ServingStatuses.Invalid,
                ExpectedCardinality = expected.Cardinality,
                ActualCardinality = traversal.Discovered.Length,
                ExpectedVisited = expected.Visited,
                ActualVisited = traversal.VisitedCount,
                ExpectedDigest = expected.Digest,
                ActualDigest = digest,
            };
        }
        catch (Exception ex)
        {
            return new G1Result
            {
                Op = "G1",
                Seq = probe.Seq,
                Stratum = probe.Stratum,
                Measured = probe.Measured,
                Status = ServingStatuses.Error,
                ExpectedCardinality = expected.Cardinality,
                ExpectedVisited = expected.Visited,
                ErrorMessage = ex.Message,
            };
        }
    }
}
