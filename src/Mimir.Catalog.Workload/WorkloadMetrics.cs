using System.Text.Json;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Neutral metric helpers for the future candidate harness. Frozen semantics:
/// per (operation, stratum, repetition) latency summaries use p50/p90/p95/p99
/// (p99.9 excluded in v1); the authoritative cross-repetition summary is the
/// median of the corresponding repetition-level summary metrics.
/// </summary>
public static class WorkloadMetrics
{
    public static double Percentile(IReadOnlyList<double> sortedAscending, double q)
    {
        if (sortedAscending.Count == 0) throw new ArgumentException("no samples");
        if (q <= 0) return sortedAscending[0];
        if (q >= 1) return sortedAscending[^1];
        double pos = q * (sortedAscending.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedAscending[lo];
        double frac = pos - lo;
        return sortedAscending[lo] + (sortedAscending[hi] - sortedAscending[lo]) * frac;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return Percentile(sorted, 0.5);
    }

    /// <summary>Authoritative summary = median of the repetition-level summaries (per statistic).</summary>
    public static double MedianOfSummaries(IReadOnlyList<double> repetitionValues) => Median(repetitionValues);

    public static double ThroughputPerSecond(long operations, double wallSeconds)
        => wallSeconds > 0 ? operations / wallSeconds : 0;
}
