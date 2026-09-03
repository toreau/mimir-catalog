namespace Mimir.Catalog.Workload;

/// <summary>
/// Deterministic breadth-first ancestry traversal owned by the benchmark
/// harness. Storage adapters expose only S5-style parent adjacency; recursive
/// SQL or candidate-owned traversal is not part of the workload.
///
/// Semantics (frozen): single start QID; start node depth 0; parents of a node
/// at depth d sit at depth d+1; nodes at depth MaxDepth are not expanded;
/// adjacency results are processed in ascending QID order; one visited set
/// deduplicates; the start QID is not automatically part of the result (a
/// cycle returning it is suppressed by the visited set). The maximum distinct
/// visited structural nodes is VisitedNodeGuard; an expansion that would
/// exceed it is rejected during workload generation (never order-dependent
/// truncation of a candidate run).
/// </summary>
public static class GraphTraversal
{
    public sealed class Result
    {
        public required long Start { get; init; }
        public required long[] Discovered { get; init; } // depths 1..MaxDepth, ascending, start excluded
        public required int VisitedCount { get; init; }
        public required int MaxReachedDepth { get; init; }
        public required bool ExceededGuard { get; init; }
    }

    public static Result Ancestry(long start, int maxDepth, long guard, Func<long, long[]> parents)
    {
        if (maxDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        var visited = new HashSet<long> { start };
        var discovered = new List<long>();
        var frontier = new Queue<(long Node, int Depth)>();
        frontier.Enqueue((start, 0));
        int maxReached = 0;
        bool exceeded = false;

        while (frontier.Count > 0)
        {
            var (node, depth) = frontier.Dequeue();
            if (depth >= maxDepth) continue;
            if (depth + 1 > maxReached) maxReached = depth + 1;

            long[] ps = parents(node) ?? Array.Empty<long>();
            foreach (long p in ps) // adjacency already ascending
            {
                if (visited.Contains(p)) continue;
                if (visited.Count >= guard)
                {
                    exceeded = true;
                    break;
                }
                visited.Add(p);
                discovered.Add(p);
                frontier.Enqueue((p, depth + 1));
            }
            if (exceeded) break;
        }

        if (exceeded)
        {
            return new Result { Start = start, Discovered = discovered.ToArray(), VisitedCount = visited.Count, MaxReachedDepth = maxReached, ExceededGuard = true };
        }

        discovered.Sort();
        return new Result
        {
            Start = start,
            Discovered = discovered.ToArray(),
            VisitedCount = visited.Count,
            MaxReachedDepth = maxReached,
            ExceededGuard = false,
        };
    }
}
