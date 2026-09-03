namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral logical adapter boundary for the storage bake-off.
/// No SQLite (or any storage-engine) types appear on this surface; results are
/// fully materialized logical data so the later harness can time complete
/// result retrieval. G1/G2 traversal is harness-owned and never part of a
/// storage adapter.
/// </summary>

public readonly record struct ConceptHit(bool Present, bool InT1, bool InT2);

/// <summary>Full logical Concept row for A1 folding: (Qid, InT1, InT2).</summary>
public readonly record struct ConceptRow(long Qid, bool InT1, bool InT2);

public readonly record struct LexicalHit(long Qid, string LexKind);

public readonly record struct LexicalRow(long Qid, string Lang, string LexKind, string Value);

public readonly record struct EdgeRow(long SubjectQid, long TargetQid);

public readonly record struct A5Row(long TargetQid, long Fanout, string? EnLabel, string? NbLabel);

public interface IStorageCandidate : IDisposable
{
    void Open();

    ConceptHit GetConcept(long qid);

    IReadOnlyList<LexicalHit> LookupLexical(string lang, string value);

    IReadOnlyList<LexicalRow> GetLexicalByQid(long qid);

    IReadOnlyList<long> GetInstanceOf(long subjectQid);

    IReadOnlyList<long> GetSubclassOf(long subjectQid);
}

public enum AnalyticalOperation
{
    A1Concept,
    A1LexicalEntry,
    A1InstanceOf,
    A1SubclassOf,
    A2,
    A3,
    A4,
    A5,
}

/// <summary>
/// Minimum analytical boundary. A1 scan methods stream logical rows that the
/// neutral harness folds with MultisetFoldV1; A2-A5 return grouped logical
/// rows that the neutral harness canonicalizes/digests. Exact timing rules
/// (streaming materialization) are finalized in the harness slice.
/// </summary>
public interface IAnalyticalCandidate : IDisposable
{
    void Open();

    IEnumerable<ConceptRow> ScanConcept();

    IEnumerable<LexicalRow> ScanLexicalEntry();

    IEnumerable<EdgeRow> ScanInstanceOf();

    IEnumerable<EdgeRow> ScanSubclassOf();

    IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts();

    IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout();

    IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout();

    IReadOnlyList<A5Row> A5P31TargetLabels();
}
