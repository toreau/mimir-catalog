using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Executes one full 200-concept G2 batch as a single neutral graph operation.
/// P31 targets are processed in ascending order with multiplicity preserved
/// (each occurrence traversed); the resulting structural QID set is deduplicated
/// via SortedSet, matching the frozen generator. No correctness digests are
/// computed here.
/// </summary>
public sealed class G2OperationExecutor
{
    public const int MaxDepth = 3;
    public const long Guard = 5000;

    private readonly IStorageCandidate _candidate;

    public G2OperationExecutor(IStorageCandidate candidate) => _candidate = candidate;

    public IReadOnlyList<G2PerInputExecutionOutcome> Execute(IReadOnlyList<G2Concept> concepts)
    {
        var results = new List<G2PerInputExecutionOutcome>(concepts.Count);
        for (int item = 0; item < concepts.Count; item++)
        {
            var concept = concepts[item];
            try
            {
                var targets = _candidate.GetInstanceOf(concept.Qid).OrderBy(x => x).ToArray();
                var structural = new SortedSet<long>();
                bool ok = true;
                foreach (long tg in targets)
                {
                    structural.Add(tg);
                    var trav = GraphTraversal.Ancestry(
                        tg,
                        maxDepth: MaxDepth,
                        guard: Guard,
                        parents: qid => _candidate.GetSubclassOf(qid).OrderBy(x => x).ToArray());
                    if (trav.ExceededGuard)
                    {
                        results.Add(Failed(item, concept, "guard exceeded (5000) during G2 ancestry"));
                        ok = false;
                        break;
                    }
                    foreach (long a in trav.Discovered) structural.Add(a);
                }
                if (ok)
                    results.Add(new G2PerInputExecutionOutcome
                    {
                        Item = item,
                        Qid = concept.Qid,
                        SourceStratum = concept.SourceStratum,
                        StructuralQidsAscending = structural.ToArray(),
                    });
            }
            catch (Exception ex)
            {
                results.Add(Failed(item, concept, ex.Message));
            }
        }
        return results;
    }

    private static G2PerInputExecutionOutcome Failed(int item, G2Concept concept, string message)
        => new() { Item = item, Qid = concept.Qid, SourceStratum = concept.SourceStratum, ErrorMessage = message };
}
