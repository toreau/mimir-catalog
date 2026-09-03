using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Bounded-memory A1 executor: consumes the candidate's lazy relation scan
/// exactly once, canonical-encodes every row, accumulates MultisetFoldV1, and
/// returns only {Operation, ActualRowCount, ActualDigest}. No full-relation
/// materialization. A future timer may wrap Execute for the complete A1
/// operation; expected comparison stays outside.
/// </summary>
public sealed class A1OperationExecutor
{
    private readonly IAnalyticalCandidate _candidate;

    public A1OperationExecutor(IAnalyticalCandidate candidate) => _candidate = candidate;

    public A1ExecutionResult Execute(string operation)
    {
        var fold = new MultisetFoldV1();
        switch (operation)
        {
            case "A1-Concept":
                foreach (var r in _candidate.ScanConcept())
                    fold.Add(MultisetFoldV1.ConceptRow(r.Qid, r.InT1, r.InT2));
                break;
            case "A1-LexicalEntry":
                foreach (var r in _candidate.ScanLexicalEntry())
                    fold.Add(MultisetFoldV1.LexicalRow(r.Qid, r.Lang, r.LexKind, r.Value));
                break;
            case "A1-InstanceOf":
                foreach (var r in _candidate.ScanInstanceOf())
                    fold.Add(MultisetFoldV1.EdgeRow(r.SubjectQid, r.TargetQid));
                break;
            case "A1-SubclassOf":
                foreach (var r in _candidate.ScanSubclassOf())
                    fold.Add(MultisetFoldV1.EdgeRow(r.SubjectQid, r.TargetQid));
                break;
            default:
                throw new InvalidOperationException($"unsupported A1 operation {operation}");
        }
        return new A1ExecutionResult { Operation = operation, ActualRowCount = fold.Count, ActualDigest = fold.Digest() };
    }
}
