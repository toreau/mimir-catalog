namespace Mimir.Catalog.Workload;

/// <summary>
/// Deterministic guard-fitted G2 batch selection. Candidate counts
/// (considered/rejected) are candidate counts; G2MaxVisitedAccepted is folded
/// only from the local maxima of candidates that are accepted, never from a
/// candidate later rejected on a subsequent ancestry root.
/// </summary>
public static class G2BatchSelection
{
    public sealed class Outcome
    {
        public List<(long Qid, string Source)> Inputs { get; } = new();
        public int Considered { get; set; }
        public int Rejected { get; set; }
        public long MaxVisitedAccepted { get; set; }
        public List<string> Shortfalls { get; } = new();
    }

    public static Outcome Select(
        IReadOnlyList<(string Stratum, long Count, IReadOnlyList<long> Pool)> specs,
        Func<long, long[]?> instanceTargets,
        Func<long, long[]> subclassParents,
        string domain,
        int maxDepth,
        long guard)
    {
        var o = new Outcome();
        foreach (var (stratum, count, pool) in specs)
        {
            string source = stratum switch
            {
                "P31Degree1" => "P31Degree1",
                "P31Degree2Plus" => "P31Degree2Plus",
                _ => throw new InvalidDataException($"unsupported G2 stratum {stratum}"),
            };
            long acceptedHere = 0;
            foreach (var it in WorkloadSelection.RankTopQids(pool, domain, "G2", source, pool.Count))
            {
                o.Considered++;
                long candMax = 0;
                bool fits = true;
                long[]? targets = instanceTargets(it.Qid);
                if (targets != null)
                {
                    foreach (long tg in targets)
                    {
                        var trav = GraphTraversal.Ancestry(tg, maxDepth, guard, subclassParents);
                        if (trav.ExceededGuard) { fits = false; break; }
                        if (trav.VisitedCount > candMax) candMax = trav.VisitedCount;
                    }
                }
                if (!fits) { o.Rejected++; continue; }
                o.Inputs.Add((it.Qid, source));
                if (candMax > o.MaxVisitedAccepted) o.MaxVisitedAccepted = candMax;
                acceptedHere++;
                if (acceptedHere >= count) break;
            }
            if (acceptedHere < count)
                o.Shortfalls.Add($"G2/{stratum}: only {acceptedHere}/{count} ranked concepts fit the guard");
        }
        return o;
    }
}
