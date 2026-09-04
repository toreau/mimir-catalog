using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// G1 cross-repetition aggregation over in-memory repetition summaries. Consumes
/// only GraphWorkload + G1RepetitionSummary records; never reads child artifacts,
/// never aggregates raw samples, never runs processes. G1ComparisonReady is
/// intentionally independent of input-integrity/evidence/I/O axes.
///
/// Diagnostic rule (deliberate improvement over serving): a repetition counts as
/// comparison-valid only when it both claims Status=Valid AND passes shape
/// validation; malformed records never inflate ValidRepetitionCount nor appear
/// in IncompleteRepetitions.
/// </summary>
public static class G1CrossSummaryCalculator
{
    private const string Operation = "G1";

    public static G1CrossCalculationResult Calculate(
        GraphWorkload workload,
        IReadOnlyList<G1RepetitionSummary> summaries)
    {
        var expected = ExpectedGroups(workload);
        var problems = new List<G1CrossIntegrityProblem>();

        if (expected.Count == 0)
        {
            problems.Add(new G1CrossIntegrityProblem("", "", null, G1CrossIntegrityCode.NoExpectedCrossSummaries));
            return new G1CrossCalculationResult(false, problems, Array.Empty<G1CrossSummary>(), false);
        }

        var byKey = summaries
            .GroupBy(s => (s.Operation, s.Stratum))
            .ToDictionary(g => g.Key, g => g.ToList());

        var cross = new List<G1CrossSummary>();
        foreach (string stratum in expected.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            long expectedCount = expected[stratum];
            bool hasRecs = byKey.TryGetValue((Operation, stratum), out var recs);
            var matrix = new G1RepetitionSummary?[4]; // index 1..3
            bool matrixCorrupt = false;
            var shapeValid = new Dictionary<int, bool>();

            if (recs is null || recs.Count == 0)
            {
                problems.Add(new G1CrossIntegrityProblem(Operation, stratum, null, G1CrossIntegrityCode.MissingRepetitionSummary));
                cross.Add(IncompleteCross(stratum, expectedCount, 0, Array.Empty<int>()));
                continue;
            }

            foreach (var rec in recs)
            {
                if (rec.Repetition is < 1 or > 3)
                {
                    problems.Add(new G1CrossIntegrityProblem(Operation, stratum, rec.Repetition, G1CrossIntegrityCode.UnexpectedRepetitionNumber));
                    continue; // never shape-validated, never enters the matrix
                }
                bool ok = ValidateShape(rec, expectedCount, problems);
                shapeValid[rec.Repetition] = ok;
                if (matrix[rec.Repetition] is not null)
                {
                    problems.Add(new G1CrossIntegrityProblem(Operation, stratum, rec.Repetition, G1CrossIntegrityCode.DuplicateRepetitionSummary));
                    matrixCorrupt = true;
                    continue;
                }
                matrix[rec.Repetition] = rec;
            }

            if (matrixCorrupt || matrix[1] is null || matrix[2] is null || matrix[3] is null)
            {
                for (int rep = 1; rep <= 3; rep++)
                    if (matrix[rep] is null)
                        problems.Add(new G1CrossIntegrityProblem(Operation, stratum, rep, G1CrossIntegrityCode.MissingRepetitionSummary));
                cross.Add(IncompleteCross(stratum, expectedCount, 0, Array.Empty<int>()));
                continue;
            }

            bool allComparisonValid = true;
            var incompleteReps = new List<int>();
            int validCount = 0;
            for (int rep = 1; rep <= 3; rep++)
            {
                bool ok = shapeValid[rep];
                if (!ok)
                {
                    allComparisonValid = false;
                    continue; // malformed records count nowhere
                }
                var rec = matrix[rep]!;
                if (rec.Status == G1SummaryStatus.Incomplete)
                {
                    allComparisonValid = false;
                    incompleteReps.Add(rep);
                }
                else
                {
                    validCount++;
                }
            }
            incompleteReps.Sort();

            if (allComparisonValid)
            {
                var valid = new[] { matrix[1]!, matrix[2]!, matrix[3]! };
                var metrics = new G1SummaryMetrics(
                    expectedCount,
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.MinSeconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.P50Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.P90Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.P95Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.P99Seconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.MaxSeconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.MeanSeconds).ToList()),
                    WorkloadMetrics.MedianOfSummaries(valid.Select(m => m.Metrics!.ThroughputPerSecond).ToList()));
                cross.Add(new G1CrossSummary(Operation, stratum, G1CrossSummaryStatus.Valid, expectedCount, 3, Array.Empty<int>(), metrics));
            }
            else
            {
                cross.Add(IncompleteCross(stratum, expectedCount, validCount, incompleteReps));
            }
        }

        // Unexpected identities never contaminate expected groups.
        var expectedKeys = expected.Keys.Select(k => (Operation, k)).ToHashSet();
        var unexpectedSeen = new HashSet<(string, string, int)>();
        foreach (var s in summaries)
        {
            if (expectedKeys.Contains((s.Operation, s.Stratum))) continue;
            if (!unexpectedSeen.Add((s.Operation, s.Stratum, s.Repetition))) continue;
            problems.Add(new G1CrossIntegrityProblem(s.Operation, s.Stratum, s.Repetition, G1CrossIntegrityCode.UnexpectedRepetitionSummary));
        }

        problems.Sort(Comparer<G1CrossIntegrityProblem>.Create((a, b) =>
        {
            int op = OpRank(a.Operation).CompareTo(OpRank(b.Operation));
            if (op != 0) return op;
            // Total ordering: after G1-first, unknown operation names sort
            // ordinally (never treated as equal regardless of stratum/etc).
            int opName = StringComparer.Ordinal.Compare(a.Operation, b.Operation);
            if (opName != 0) return opName;
            int str = StringComparer.Ordinal.Compare(a.Stratum, b.Stratum);
            if (str != 0) return str;
            int rep = (a.Repetition ?? int.MinValue).CompareTo(b.Repetition ?? int.MinValue);
            return rep != 0 ? rep : a.Code.CompareTo(b.Code);
        }));

        bool ready = cross.Count > 0 && cross.All(c => c.Status == G1CrossSummaryStatus.Valid);
        return new G1CrossCalculationResult(problems.Count == 0, problems, cross, ready);
    }

    private static bool ValidateShape(G1RepetitionSummary rec, long expectedCount, List<G1CrossIntegrityProblem> problems)
    {
        bool ok = true;
        void Bad(G1CrossIntegrityCode code)
        {
            problems.Add(new G1CrossIntegrityProblem(rec.Operation, rec.Stratum, rec.Repetition, code));
            ok = false;
        }

        if (rec.ExpectedCount != expectedCount) Bad(G1CrossIntegrityCode.ExpectedCountMismatch);

        if (rec.Status == G1SummaryStatus.Valid)
        {
            if (rec.Reasons.Count != 0) Bad(G1CrossIntegrityCode.ValidSummaryHasReasons);
            if (rec.Metrics is null) Bad(G1CrossIntegrityCode.ValidSummaryMissingMetrics);
            else if (rec.Metrics.Count != expectedCount) Bad(G1CrossIntegrityCode.ValidMetricCountMismatch);
            if (rec.ObservedCount != expectedCount || rec.ValidCount != expectedCount
                || rec.InvalidCount != 0 || rec.TimeoutCount != 0 || rec.ErrorCount != 0)
                Bad(G1CrossIntegrityCode.ValidSummaryCountsMismatch);
        }
        else
        {
            if (rec.Reasons.Count == 0) Bad(G1CrossIntegrityCode.IncompleteSummaryMissingReasons);
            if (rec.Metrics is not null) Bad(G1CrossIntegrityCode.IncompleteSummaryHasMetrics);
        }
        return ok;
    }

    private static int OpRank(string operation) => operation == Operation ? 0 : 1;

    private static G1CrossSummary IncompleteCross(string stratum, long expectedCount, int validCount, IReadOnlyList<int> incompleteReps)
        => new(Operation, stratum, G1CrossSummaryStatus.Incomplete, expectedCount, validCount, incompleteReps, null);

    private static Dictionary<string, long> ExpectedGroups(GraphWorkload workload)
        => workload.Probes
            .Where(p => p.Op == Operation && p.Measured)
            .GroupBy(p => p.Stratum)
            .ToDictionary(g => g.Key, g => g.LongCount());
}
