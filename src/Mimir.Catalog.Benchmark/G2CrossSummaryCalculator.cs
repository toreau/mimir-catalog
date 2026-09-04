using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// G2 cross-repetition aggregation over in-memory repetition summaries. Consumes
/// only G2RepetitionSummary records; no workload reload, no child/raw evidence,
/// no process/resource inspection, no timeout reclassification. The one expected
/// G2 group is the fixed 1..3 matrix. G2ComparisonReady is intentionally
/// independent of input-integrity/evidence/I/O axes.
///
/// Only BatchWallSeconds from three shape-valid Status=Valid summaries can reach
/// the median; ObservedDiagnosticWallSeconds is never a fallback.
/// </summary>
public static class G2CrossSummaryCalculator
{
    private const string Operation = "G2";

    public static G2CrossCalculationResult Calculate(IReadOnlyList<G2RepetitionSummary> summaries)
    {
        var problems = new List<G2CrossIntegrityProblem>();
        var matrix = new G2RepetitionSummary?[4]; // index 1..3
        var shapeValid = new Dictionary<int, bool>();
        bool matrixCorrupt = false;
        var unexpectedSeen = new HashSet<(string, int)>();

        foreach (var rec in summaries)
        {
            if (rec.Operation != Operation)
            {
                if (unexpectedSeen.Add((rec.Operation, rec.Repetition)))
                    problems.Add(new G2CrossIntegrityProblem(rec.Operation, rec.Repetition, G2CrossIntegrityCode.UnexpectedRepetitionSummary));
                continue;
            }
            if (rec.Repetition is < 1 or > 3)
            {
                problems.Add(new G2CrossIntegrityProblem(Operation, rec.Repetition, G2CrossIntegrityCode.UnexpectedRepetitionNumber));
                continue; // never shape-validated, never enters the matrix
            }
            bool ok = ValidateShape(rec, problems);
            shapeValid[rec.Repetition] = ok;
            if (matrix[rec.Repetition] is not null)
            {
                problems.Add(new G2CrossIntegrityProblem(Operation, rec.Repetition, G2CrossIntegrityCode.DuplicateRepetitionSummary));
                matrixCorrupt = true;
                continue;
            }
            matrix[rec.Repetition] = rec;
        }

        for (int rep = 1; rep <= 3; rep++)
            if (matrix[rep] is null)
                problems.Add(new G2CrossIntegrityProblem(Operation, rep, G2CrossIntegrityCode.MissingRepetitionSummary));

        if (matrixCorrupt || matrix[1] is null || matrix[2] is null || matrix[3] is null)
        {
            var incomplete = CorruptedCross();
            return new G2CrossCalculationResult(problems.Count == 0, Sorted(problems), incomplete, false);
        }

        bool allShapeValid = true;
        bool allValidStatus = true;
        var incompleteReps = new List<int>();
        int validCount = 0;
        for (int rep = 1; rep <= 3; rep++)
        {
            bool ok = shapeValid[rep];
            if (!ok) { allShapeValid = false; continue; } // malformed counts nowhere
            var rec = matrix[rep]!;
            if (rec.Status == G2SummaryStatus.Incomplete)
            {
                allValidStatus = false;
                incompleteReps.Add(rep);
            }
            else
            {
                validCount++;
            }
        }
        incompleteReps.Sort();

        int e0 = matrix[1]!.ExpectedPerInputCount;
        bool countsAgree = matrix[2]!.ExpectedPerInputCount == e0 && matrix[3]!.ExpectedPerInputCount == e0;
        if (!countsAgree)
            problems.Add(new G2CrossIntegrityProblem(Operation, null, G2CrossIntegrityCode.ExpectedPerInputCountMismatch));

        bool crossValid = allShapeValid && allValidStatus && countsAgree;
        if (!crossValid)
        {
            var incomplete = new G2CrossSummary(Operation, G2CrossSummaryStatus.Incomplete,
                countsAgree ? e0 : null, validCount, incompleteReps, null);
            return new G2CrossCalculationResult(problems.Count == 0, Sorted(problems), incomplete, false);
        }

        var valid = new[] { matrix[1]!, matrix[2]!, matrix[3]! };
        double median = WorkloadMetrics.MedianOfSummaries(new[]
        {
            valid[0].BatchWallSeconds!.Value,
            valid[1].BatchWallSeconds!.Value,
            valid[2].BatchWallSeconds!.Value,
        });
        var summary = new G2CrossSummary(Operation, G2CrossSummaryStatus.Valid, e0, 3, Array.Empty<int>(), median);
        return new G2CrossCalculationResult(problems.Count == 0, Sorted(problems), summary, true);
    }

    private static bool ValidateShape(G2RepetitionSummary rec, List<G2CrossIntegrityProblem> problems)
    {
        bool ok = true;
        void Bad(G2CrossIntegrityCode code)
        {
            problems.Add(new G2CrossIntegrityProblem(rec.Operation, rec.Repetition, code));
            ok = false;
        }

        if (rec.Status == G2SummaryStatus.Valid)
        {
            if (rec.Reasons.Count != 0) Bad(G2CrossIntegrityCode.ValidSummaryHasReasons);
            if (rec.ObservedPerInputCount != rec.ExpectedPerInputCount) Bad(G2CrossIntegrityCode.ValidSummaryObservedCountMismatch);
            if (rec.BatchWallSeconds is null) Bad(G2CrossIntegrityCode.ValidSummaryMissingAuthoritativeWall);
            if (rec.ObservedDiagnosticWallSeconds is null) Bad(G2CrossIntegrityCode.ValidSummaryMissingDiagnosticWall);
            if (rec.BatchWallSeconds is not null && rec.ObservedDiagnosticWallSeconds is not null
                && rec.BatchWallSeconds != rec.ObservedDiagnosticWallSeconds)
                Bad(G2CrossIntegrityCode.ValidSummaryWallMismatch);
            if (rec.TimedStatus != TimedResultStatus.Valid) Bad(G2CrossIntegrityCode.ValidSummaryTimedStatusNotValid);
            if (rec.ChildCorrectness != ServingStatuses.Valid) Bad(G2CrossIntegrityCode.ValidSummaryChildCorrectnessNotValid);
        }
        else
        {
            if (rec.Reasons.Count == 0) Bad(G2CrossIntegrityCode.IncompleteSummaryMissingReasons);
            if (rec.BatchWallSeconds is not null) Bad(G2CrossIntegrityCode.IncompleteSummaryHasAuthoritativeWall);
        }
        return ok;
    }

    private static IReadOnlyList<G2CrossIntegrityProblem> Sorted(List<G2CrossIntegrityProblem> problems)
    {
        problems.Sort(Comparer<G2CrossIntegrityProblem>.Create((a, b) =>
        {
            int opRank = (a.Operation == Operation ? 0 : 1).CompareTo(b.Operation == Operation ? 0 : 1);
            if (opRank != 0) return opRank;
            int name = StringComparer.Ordinal.Compare(a.Operation, b.Operation);
            if (name != 0) return name;
            int rep = (a.Repetition ?? int.MinValue).CompareTo(b.Repetition ?? int.MinValue);
            return rep != 0 ? rep : a.Code.CompareTo(b.Code);
        }));
        return problems;
    }

    private static G2CrossSummary CorruptedCross()
        => new(Operation, G2CrossSummaryStatus.Incomplete, null, 0, Array.Empty<int>(), null);
}
