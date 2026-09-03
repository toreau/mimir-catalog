using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class ServingRunnerTests
{
    private sealed class FakeAdapter : IStorageCandidate
    {
        public Func<long, ConceptHit> GetC { get; set; } = _ => new ConceptHit(false, false, false);
        public Func<string, string, IReadOnlyList<LexicalHit>> Lex { get; set; } = (_, _) => Array.Empty<LexicalHit>();
        public Func<long, IReadOnlyList<LexicalRow>> LexQ { get; set; } = _ => Array.Empty<LexicalRow>();
        public Func<long, IReadOnlyList<long>> Inst { get; set; } = _ => Array.Empty<long>();
        public Func<long, IReadOnlyList<long>> Sub { get; set; } = _ => Array.Empty<long>();
        public void Open() { }
        public void Dispose() { }
        public ConceptHit GetConcept(long q) => GetC(q);
        public IReadOnlyList<LexicalHit> LookupLexical(string l, string v) => Lex(l, v);
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long q) => LexQ(q);
        public IReadOnlyList<long> GetInstanceOf(long q) => Inst(q);
        public IReadOnlyList<long> GetSubclassOf(long q) => Sub(q);
    }

    private static ServingProbe P(string op, long seq, string stratum, long? qid = null, string? lang = null, string? value = null, bool measured = true)
        => new(op, seq, stratum, measured, qid, lang, value);

    private static ServingExpected E(string op, long seq, long card, string digest, bool measured = true)
        => new(op, seq, measured, card, digest);

    private static ServingWorkload W(IEnumerable<(ServingProbe P, ServingExpected E)> items)
    {
        var list = items.ToList();
        return new ServingWorkload
        {
            Probes = list.Select(i => i.P).ToList(),
            Expected = list.ToDictionary(i => (i.P.Op, i.P.Seq), i => i.E),
        };
    }

    private static ProbeResult Run(FakeAdapter a, ServingWorkload w) => new ServingCorrectnessRunner(a).RunAll(w).Single();

    [Fact]
    public void S1_HitAndMiss_Valid()
    {
        var a = new FakeAdapter { GetC = q => q == 1 ? new ConceptHit(true, true, false) : new ConceptHit(false, false, false) };
        var w = W([
            (P("S1", 0, "T1Only", qid: 1), E("S1", 0, 1, WorkloadOracle.ConceptResultDigest(1, true, true, false))),
            (P("S1", 1, "Absent", qid: 99), E("S1", 1, 0, WorkloadOracle.ConceptResultDigest(99, false, false, false))),
        ]);
        var results = new ServingCorrectnessRunner(a).RunAll(w);
        Assert.All(results, r => Assert.Equal(ServingStatuses.Valid, r.Status));
        Assert.True(results[0].Measured);
    }

    [Fact]
    public void S2_MissUsesMissDigest_AndMisbehaviourInvalid()
    {
        var a = new FakeAdapter { Lex = (l, v) => Array.Empty<LexicalHit>() };
        var miss = WorkloadOracle.LexMissDigest("nb", "q");
        var w = W([
            (P("S2", 0, "Miss", lang: "nb", value: "q"), E("S2", 0, 0, miss)),
        ]);
        Assert.Equal(ServingStatuses.Valid, Run(a, w).Status);

        // Miss probe that unexpectedly returns members -> INVALID.
        var w2 = W([
            (P("S2", 0, "Miss", lang: "nb", value: "q"), E("S2", 0, 0, miss)),
        ]);
        a.Lex = (_, _) => new[] { new LexicalHit(1, "label") };
        Assert.Equal(ServingStatuses.Invalid, Run(a, w2).Status);

        // Non-miss probe with zero rows -> INVALID against expected hit.
        a.Lex = (_, _) => Array.Empty<LexicalHit>();
        var memberDigest = WorkloadOracle.LexMembersDigest(new[] { (1L, "label") });
        var w3 = W([(P("S2", 0, "Fanout1", lang: "en", value: "x"), E("S2", 0, 1, memberDigest))]);
        Assert.Equal(ServingStatuses.Invalid, Run(a, w3).Status);
    }

    [Fact]
    public void S2_DuplicateMultiplicity_AffectsCorrectness()
    {
        var a = new FakeAdapter { Lex = (_, _) => new[] { new LexicalHit(1, "label"), new LexicalHit(1, "label") } };
        string two = WorkloadOracle.LexMembersDigest(new[] { (1L, "label"), (1L, "label") });
        string one = WorkloadOracle.LexMembersDigest(new[] { (1L, "label") });
        var w = W([(P("S2", 0, "Fanout1", lang: "en", value: "x"), E("S2", 0, 2, two))]);
        Assert.Equal(ServingStatuses.Valid, Run(a, w).Status);
        var wBad = W([(P("S2", 0, "Fanout1", lang: "en", value: "x"), E("S2", 0, 1, one))]);
        Assert.Equal(ServingStatuses.Invalid, Run(a, wBad).Status);
    }

    [Fact]
    public void S3_WrongReturnedQid_Invalid()
    {
        var a = new FakeAdapter { LexQ = _ => new[] { new LexicalRow(99, "en", "label", "Alpha") } };
        string ok = WorkloadOracle.LexicalRowsDigest(1, new[] { ("en", "label", "Alpha") });
        var w = W([(P("S3", 0, "T1WithLexical", qid: 1), E("S3", 0, 1, ok))]);
        Assert.Equal(ServingStatuses.Invalid, Run(a, w).Status);
    }

    [Fact]
    public void S4S5_Unsorted_Canonicalized_Valid()
    {
        var a = new FakeAdapter { Inst = _ => new List<long> { 20, 5, 9 }, Sub = _ => new List<long> { 40, 7 } };
        var w = W([
            (P("S4", 0, "Degree1", qid: 1), E("S4", 0, 3, WorkloadOracle.AdjacencyDigest(new long[] { 5, 9, 20 }))),
            (P("S5", 1, "Degree1", qid: 2), E("S5", 1, 2, WorkloadOracle.AdjacencyDigest(new long[] { 7, 40 }))),
        ]);
        var results = new ServingCorrectnessRunner(a).RunAll(w);
        Assert.All(results, r => Assert.Equal(ServingStatuses.Valid, r.Status));
        Assert.Equal(results[0].ActualCardinality, 3);
    }

    [Fact]
    public void DigestAndCardinalityMismatch_Invalid()
    {
        var a = new FakeAdapter { GetC = _ => new ConceptHit(true, true, false) };
        var wrongDigest = E("S1", 0, 1, WorkloadOracle.ConceptResultDigest(1, true, true, false) == "0".PadRight(64, '0') ? "1".PadRight(64, '1') : "0".PadRight(64, '0'));
        var w = W([(P("S1", 0, "T1Only", qid: 1), wrongDigest)]);
        Assert.Equal(ServingStatuses.Invalid, Run(a, w).Status);

        var wrongCard = E("S1", 0, 0, WorkloadOracle.ConceptResultDigest(1, true, true, false));
        var w2 = W([(P("S1", 0, "T1Only", qid: 1), wrongCard)]);
        Assert.Equal(ServingStatuses.Invalid, Run(a, w2).Status);
    }

    [Fact]
    public void AdapterException_Error()
    {
        var a = new FakeAdapter { GetC = _ => throw new InvalidOperationException("boom") };
        var w = W([(P("S1", 0, "T1Only", qid: 1), E("S1", 0, 1, WorkloadOracle.ConceptResultDigest(1, true, true, false)))]);
        var r = Run(a, w);
        Assert.Equal(ServingStatuses.Error, r.Status);
        Assert.NotNull(r.ErrorMessage);
    }

    [Fact]
    public void Tail_MeasuredFalse_Preserved_AndValid()
    {
        var a = new FakeAdapter { GetC = _ => new ConceptHit(true, false, true) };
        var w = W([(P("S1", 0, "Tail", qid: 99, measured: false), E("S1", 0, 1, WorkloadOracle.ConceptResultDigest(99, true, false, true), measured: false))]);
        var r = Run(a, w);
        Assert.Equal(ServingStatuses.Valid, r.Status);
        Assert.False(r.Measured);
        Assert.Equal("S1", r.Op);
        Assert.Equal("Tail", r.Stratum);
    }
}
