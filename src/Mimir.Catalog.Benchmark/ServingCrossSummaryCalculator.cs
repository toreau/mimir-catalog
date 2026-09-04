using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Cross-repetition aggregation over in-memory repetition summaries. Consumes
/// only ServingWorkload + ServingRepetitionSummary records; never reads child
/// artifacts, never aggregates raw samples and never runs processes.
/// ServingComparisonReady is independent of evidence/integrity/I/O axes.
/// </summary>
public static class ServingCrossSummaryCalculator
{
    private static readonly string[] Operations = { "S1", "S2", "S3", "S4", "S5" };

    public static ServingCrossCalculationResult Calculate(
        ServingWorkload workload,
        IReadOnlyList<ServingRepetitionSummary> summaries)
    {
        var expected = ExpectedKeys(workload);
        var problems = new List<ServingIntegrityProblem>();

        if (expected.Count == 0)
        {
            problems.Add(new ServingIntegrityProblem("", "", null, ServingCrossIntegrityCode.NoExpectedCrossSummaries));
            return new ServingCrossCalculationResult(false, problems, Array.Empty<ServingCrossSummary>(), false);
        }

        var byKey = summaries
            .GroupBy(s => (s.Operation, s.Stratum))
            .ToDictionary(g => g.Key, g => g.ToList());

        var cross = new List<ServingCrossSummary>();
        var orderIndex = Operations.Select((op, i) => (op, i)).ToDictionary(x => x.op, x => x.i);

        foreach ((string op, string stratum, long expectedCount) in expected)
        {
            bool hasRecs = byKey.TryGetValue((op, stratum), out var recs);
            var matrix = new ServingRepetitionSummary?[4]; // index 1..3
            bool matrixCorrupt = false;
            int shapeProblems = 0;

            if (!hasRecs || recs!.Count == 0)
            {
                problems.Add(new ServingIntegrityProblem(op, stratum, null, ServingCrossIntegrityCode.MissingRepetitionSummary));
                cross.Add(IncompleteCross(op, stratum, expectedCount, 0, Array.Empty<int>()));
                continue;
            }

            foreach (var rec in recs!)
            {
                shapeProblems += ValidateShape(rec, expectedCount, problems);
                if (rec.Repetition is < 1 or > 3)
                {
                    problems.Add(new ServingIntegrityProblem(op, stratum, rec.Repetition, ServingCrossIntegrityCode.UnexpectedRepetitionNumber));
                    continue;
                }
                if (matrix[rec.Repetition] is not null)
                {
                    problems.Add(new ServingIntegrityProblem(op, stratum, rec.Repetition, ServingCrossIntegrityCode.DuplicateRepetitionSummary));
                    matrixCorrupt = true;
                    continue;
                }
                matrix[rec.Repetition] = rec;
            }

            if (matrixCorrupt || matrix[1] is null || matrix[2] is null || matrix[3] is null)
            {
                for (int rep = 1; rep <= 3; rep++)
                    if (matrix[rep] is null)
                        problems.Add(new ServingIntegrityProblem(op, stratum, rep, ServingCrossIntegrityCode.MissingRepetitionSummary));
                cross.Add(IncompleteCross(op, stratum, expectedCount, 0, Array.Empty<int>()));
                continue;
            }

            var reps = new[] { matrix[1]!, matrix[2]!, matrix[3]! };
            if (shapeProblems > 0)
            {
                cross.Add(IncompleteCross(op, stratum, expectedCount, reps.Count(r => r.Status == ServingSummaryStatus.Valid), Array.Empty<int>()));
                continue;
            }
            if (reps.All(r => r.Status == ServingSummaryStatus.Valid))
            {
                var valid = reps.Select(r => r.Metrics!).ToList();
                var metrics = new ServingSummaryMetrics(
                    expectedCount,
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.MinSeconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.P50Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.P90Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.P95Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.P99Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.MaxSeconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.MeanSeconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.ThroughputPerSecond).ToList()));
                cross.Add(new ServingCrossSummary(op, stratum, ServingSummaryStatus.Valid, expectedCount, 3, Array.Empty<int>(), metrics));
            }
            else
            {
                var incomplete = reps.Where(r => r.Status == ServingSummaryStatus.Incomplete).Select(r => r.Repetition).OrderBy(r => r).ToList();
                cross.Add(IncompleteCross(op, stratum, expectedCount, reps.Count(r => r.Status == ServingSummaryStatus.Valid), incomplete));
            }
        }

        // Unexpected records (unknown operation/stratum) never influence expected groups.
        var expectedKeys = expected.Select(e => (e.op, e.stratum)).ToHashSet();
        var unexpected = summaries
            .Where(s => !expectedKeys.Contains((s.Operation, s.Stratum)))
            .Select(s => new { s.Operation, s.Stratum, s.Repetition })
            .Distinct()
            .OrderBy(x => (orderIndex.TryGetValue(x.Operation, out int oi) ? oi : int.MaxValue))
            .ThenBy(x => x.Stratum, StringComparer.Ordinal)
            .ThenBy(x => x.Repetition)
            .ToList();
        foreach (var rec in unexpected)
            problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.UnexpectedRepetitionSummary));

        problems.Sort(Comparer<ServingIntegrityProblem>.Create((a, b) =>
        {
            int op = (orderIndex.TryGetValue(a.Operation, out int ao) ? ao : int.MaxValue)
                .CompareTo(orderIndex.TryGetValue(b.Operation, out int bo) ? bo : int.MaxValue);
            if (op != 0) return op;
            int str = StringComparer.Ordinal.Compare(a.Stratum, b.Stratum);
            if (str != 0) return str;
            int rep = (a.Repetition ?? int.MinValue).CompareTo(b.Repetition ?? int.MinValue);
            return rep != 0 ? rep : a.Code.CompareTo(b.Code);
        }));

        bool ready = expected.Count > 0 && cross.All(c => c.Status == ServingSummaryStatus.Valid);
        return new ServingCrossCalculationResult(problems.Count == 0, problems, cross, ready);
    }

    private static int ValidateShape(ServingRepetitionSummary rec, long expectedCount, List<ServingIntegrityProblem> problems)
    {
        int added = 0;
        if (rec.ExpectedCount != expectedCount)
        {
            problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.ExpectedCountMismatch));
            added++;
        }

        if (rec.Status == ServingSummaryStatus.Valid)
        {
            if (rec.Reasons.Count != 0)
            {
                problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.ValidSummaryHasReasons));
                added++;
            }
            if (rec.Metrics is null)
            {
                problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.ValidSummaryMissingMetrics));
                added++;
            }
            else if (rec.Metrics.Count != expectedCount)
            {
                problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.ValidMetricCountMismatch));
                added++;
            }
            if (rec.ObservedCount != expectedCount || rec.ValidCount != expectedCount
                || rec.InvalidCount != 0 || rec.TimeoutCount != 0 || rec.ErrorCount != 0)
            {
                problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.ValidSummaryCountsMismatch));
                added++;
            }
        }
        else
        {
            if (rec.Reasons.Count == 0)
            {
                problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.IncompleteSummaryMissingReasons));
                added++;
            }
            if (rec.Metrics is not null)
            {
                problems.Add(new ServingIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, ServingCrossIntegrityCode.IncompleteSummaryHasMetrics));
                added++;
            }
        }
        return added;
    }

    private static List<(string op, string stratum, long expectedCount)> ExpectedKeys(ServingWorkload workload)
    {
        var keys = new List<(string, string, long)>();
        foreach (string op in Operations)
        {
            var byStratum = workload.Probes.Where(p => p.Op == op && p.Measured)
                .GroupBy(p => p.Stratum)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (Stratum: g.Key, Count: g.LongCount()))
                .ToList();
            keys.AddRange(byStratum.Select(s => (op, s.Stratum, s.Count)));
        }
        return keys;
    }

    private static ServingCrossSummary IncompleteCross(string op, string stratum, long expectedCount, int validReps, IReadOnlyList<int> incomplete)
        => new(op, stratum, ServingSummaryStatus.Incomplete, expectedCount, validReps, incomplete, null);
}
