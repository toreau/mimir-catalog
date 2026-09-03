using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Mimir.Catalog.Corpus;
using Parquet.Schema;
using Xunit;

namespace Mimir.Catalog.Corpus.Tests;

public class ParquetWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-parq-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void ConceptWriter_MultipleRowGroups_RoundTrip()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "concept.parquet");
        var w = new ConceptWriter(path, maxRows: 3);
        for (int i = 0; i < 10; i++)
            w.Add(100 + i, i % 2 == 0, i % 3 == 0);
        w.Finish();

        var insp = ParquetInspection.Inspect(path);
        Assert.Equal(10, insp.RowCount);
        Assert.True(insp.RowGroupCount >= 4);
        Assert.Equal(new[] { "Qid", "InT1", "InT2" }, insp.Columns.Select(c => c.Name));
        Assert.Equal(insp.Columns, ParquetInspection.ColumnsOf(PassBSchema.Concept));

        var qids = ParquetRead.ReadLongs(path, 0);
        var t1 = ParquetRead.ReadBools(path, 1);
        var t2 = ParquetRead.ReadBools(path, 2);
        Assert.Equal(10, qids.Length);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(100 + i, qids[i]);
            Assert.Equal(i % 2 == 0, t1[i]);
            Assert.Equal(i % 3 == 0, t2[i]);
        }
    }

    [Fact]
    public void LexicalWriter_RawStringsAndEmptyPreserved_ByteCapFlushes()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "lex.parquet");
        var w = new LexicalEntryWriter(path, maxRows: 1000, byteCap: 32);
        w.Add(1, "en", "label", "kåre-ås-中");
        w.Add(1, "en", "label", "");
        w.Add(1, "nb", "label", "veldig-lang-verdi-streng-med-veldig-mye-tekstinnhold-helt-her");
        w.Add(1, "en", "alias", "kort");
        w.Finish();

        var insp = ParquetInspection.Inspect(path);
        Assert.Equal(4, insp.RowCount);
        Assert.True(insp.RowGroupCount >= 2);

        var lang = ParquetRead.ReadStrings(path, 1);
        var kind = ParquetRead.ReadStrings(path, 2);
        var value = ParquetRead.ReadStrings(path, 3);
        Assert.Contains("kåre-ås-中", value);
        Assert.Contains("", value); // empty string preserved, not null
        Assert.Equal(4, lang.Length);
        Assert.Equal(4, kind.Length);
    }

    [Fact]
    public void EdgeWriter_RoundTrip()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "edge.parquet");
        var w = new EdgeWriter(path, maxRows: 2);
        w.Add(5, 6256);
        w.Add(5, 184);
        w.Add(107, 184);
        w.Finish();

        var insp = ParquetInspection.Inspect(path);
        Assert.Equal(3, insp.RowCount);
        Assert.Equal(new[] { "SubjectQid", "TargetQid" }, insp.Columns.Select(c => c.Name));
        Assert.Equal(insp.Columns, ParquetInspection.ColumnsOf(PassBSchema.Edge));
        Assert.Equal(new long[] { 5, 5, 107 }, ParquetRead.ReadLongs(path, 0));
        Assert.Equal(new long[] { 6256, 184, 184 }, ParquetRead.ReadLongs(path, 1));
    }

    [Fact]
    public void Schemas_AreNonNullable()
    {
        Assert.False(((DataField)PassBSchema.Concept.DataFields[0]).IsNullable);
        Assert.False(((DataField)PassBSchema.LexicalEntry.DataFields[3]).IsNullable);
        Assert.False(((DataField)PassBSchema.Edge.DataFields[1]).IsNullable);
    }

    [Fact]
    public void LexicalWriter_UsesUtf8BytesNotChars_ForByteCap()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "lex-utf8.parquet");
        // "ab" = 2 chars/2 UTF-8 bytes; "€€" = 2 chars/6 UTF-8 bytes.
        // A char-based cap of 5 would not flush; a byte-based cap of 5 must.
        var w = new LexicalEntryWriter(path, maxRows: 1000, byteCap: 5);
        w.Add(1, "en", "label", "ab");
        w.Add(1, "en", "alias", "€€");
        w.Finish();

        var insp = ParquetInspection.Inspect(path);
        Assert.Equal(2, insp.RowCount);
        Assert.True(insp.RowGroupCount >= 2, "byte cap must flush before the row that would exceed 5 UTF-8 bytes");
        Assert.Contains("€€", ParquetRead.ReadStrings(path, 3));
    }

    [Fact]
    public void PhysicalSchema_MatchesFrozen_ForAllRelations()
    {
        Directory.CreateDirectory(_dir);
        var concept = Path.Combine(_dir, "c.parquet");
        var wc = new ConceptWriter(concept, 1000);
        wc.Add(1, true, false); wc.Finish();
        Assert.Equal(ParquetInspection.ColumnsOf(PassBSchema.Concept), ParquetInspection.Inspect(concept).Columns);

        var lexical = Path.Combine(_dir, "l.parquet");
        var wl = new LexicalEntryWriter(lexical, 1000, 1 << 20);
        wl.Add(1, "en", "label", "x"); wl.Finish();
        Assert.Equal(ParquetInspection.ColumnsOf(PassBSchema.LexicalEntry), ParquetInspection.Inspect(lexical).Columns);

        var edge = Path.Combine(_dir, "e.parquet");
        var we = new EdgeWriter(edge, 1000);
        we.Add(1, 2); we.Finish();
        Assert.Equal(ParquetInspection.ColumnsOf(PassBSchema.Edge), ParquetInspection.Inspect(edge).Columns);
    }
}

public class T2IndexTests
{
    [Fact]
    public void Lookup_Seen_Unseen()
    {
        var idx = new T2Index(new long[] { 1, 5, 31, 107, 184, 197, 200, 218 });
        Assert.True(idx.Lookup(107, out int i107));
        Assert.Equal(3, i107);
        Assert.False(idx.Lookup(999, out _));

        idx.MarkSeen(3);
        idx.MarkSeen(6);
        Assert.True(idx.IsSeen(3));
        Assert.False(idx.IsSeen(1));
        Assert.Equal(2, idx.SeenCount);

        var unseen = idx.Unseen().Select(u => u.Qid).ToArray();
        Assert.Equal(new long[] { 1, 5, 31, 184, 197, 218 }, unseen);
    }

    [Fact]
    public void UnsortedInput_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => new T2Index(new long[] { 5, 1 }));
    }
}

public class PassBSyntheticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-passb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed record Rec(long Qid, string? EnLabel, string? NbLabel, string[] EnAliases, string[] NbAliases, long[] P31, long[] P279);

    private static string ItemJson(Rec r)
    {
        string Labels()
        {
            var parts = new List<string>();
            if (r.EnLabel != null) parts.Add($"\"en\":{{\"language\":\"en\",\"value\":\"{r.EnLabel}\"}}");
            if (r.NbLabel != null) parts.Add($"\"nb\":{{\"language\":\"nb\",\"value\":\"{r.NbLabel}\"}}");
            return "{" + string.Join(",", parts) + "}";
        }
        string Alias(string lang, string[] vals) => $"\"{lang}\":[" + string.Join(",", vals.Select(v => $"{{\"language\":\"{lang}\",\"value\":\"{v}\"}}")) + "]";
        string Claims()
        {
            string Claim(long prop, long val) =>
                $"{{\"mainsnak\":{{\"snaktype\":\"value\",\"property\":\"P{prop}\",\"datavalue\":{{\"value\":{{\"numeric-id\":{val}}},\"type\":\"wikibase-entityid\"}}}}}}";
            var ps = new List<string>();
            if (r.P31.Length > 0) ps.Add($"\"P31\":[{string.Join(",", r.P31.Select(v => Claim(31, v)))}]");
            if (r.P279.Length > 0) ps.Add($"\"P279\":[{string.Join(",", r.P279.Select(v => Claim(279, v)))}]");
            return "{" + string.Join(",", ps) + "}";
        }
        var aliases = (r.EnAliases.Length + r.NbAliases.Length) > 0
            ? $"\"aliases\":{{{Alias("en", r.EnAliases)}{(r.NbAliases.Length > 0 ? "," + Alias("nb", r.NbAliases) : "")}}}"
            : "\"aliases\":{}";
        return "{\"type\":\"item\",\"id\":\"Q" + r.Qid + "\",\"labels\":" + Labels() + "," + aliases + ",\"claims\":" + Claims() + "}";
    }

    private static void WriteGz(string path, IEnumerable<string> lines)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        foreach (var l in lines)
        {
            byte[] b = Encoding.UTF8.GetBytes(l + "\n");
            gz.Write(b);
        }
    }

    private static void WriteT2(string path, IEnumerable<long> endpoints)
    {
        using var fs = File.Create(path);
        Span<byte> buf = stackalloc byte[8];
        foreach (long q in endpoints.OrderBy(x => x))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, q);
            fs.Write(buf);
        }
    }

    [Fact]
    public void Synthetic_EndToEnd_ConceptUnionAndUnobservedTail()
    {
        var observed = new List<Rec>
        {
            new(1, "one", "en", new[] { "uno", "one" }, new[] { "ett" }, new long[] { 184 }, new long[] { 200 }),
            new(5, "five", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<long>(), new long[] { 184 }),
            new(31, null, "trettien", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<long>(), Array.Empty<long>()),
            new(107, "member", null, Array.Empty<string>(), Array.Empty<string>(), new long[] { 184, 5 }, new long[] { 5 }),
            new(184, "class-a", null, new[] { "ca", "class-a" }, Array.Empty<string>(), Array.Empty<long>(), Array.Empty<long>()),
            new(197, "mem", "medlem", new[] { "x", "y", "x" }, Array.Empty<string>(), Array.Empty<long>(), new long[] { 184 }),
            new(190, "mem190", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<long>(), new long[] { 31 }),
            new(218, "mem218", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<long>(), Array.Empty<long>()),
            new(900, "nine", null, Array.Empty<string>(), Array.Empty<string>(), new long[] { 5 }, new long[] { 184 }),
            new(400, "four", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<long>(), new long[] { 164 }),
        };

        // Independent expected computation from the same frozen primitives.
        var observedIds = observed.Select(o => o.Qid).ToHashSet();
        var t1 = observedIds.Where(CorpusHash.IsT1).ToHashSet();
        var p279 = observed.SelectMany(o => o.P279.Select(t => (o.Qid, t))).ToList();
        var endpoints = p279.SelectMany(e => new[] { e.Qid, e.t }).Distinct().ToHashSet();
        var conceptExpected = t1.Union(endpoints).OrderBy(x => x).ToArray();
        var t2Expected = endpoints;
        var t2OnlyExpected = endpoints.Where(q => !t1.Contains(q)).ToHashSet();

        long expectedLexical = 0;
        foreach (var o in observed)
        {
            if (!t1.Contains(o.Qid) && !endpoints.Contains(o.Qid)) continue;
            if (t1.Contains(o.Qid))
            {
                if (o.EnLabel != null) expectedLexical++;
                expectedLexical += o.EnAliases.Distinct().Count();
                if (o.NbLabel != null) expectedLexical++;
                expectedLexical += o.NbAliases.Distinct().Count();
            }
            else
            {
                if (o.EnLabel != null) expectedLexical++;
                if (o.NbLabel != null) expectedLexical++;
            }
        }
        var instanceExpected = observed
            .Where(o => t1.Contains(o.Qid))
            .SelectMany(o => o.P31.OrderBy(x => x).Select(t => (o.Qid, t)))
            .ToArray();
        var subclassExpected = observed
            .Where(o => o.P279.Length > 0)
            .SelectMany(o => o.P279.OrderBy(x => x).Select(t => (o.Qid, t)))
            .ToArray();
        long expectedUnobserved = endpoints.Count(q => !observedIds.Contains(q)); // Q200 and Q164 are unobserved T2 endpoints

        Directory.CreateDirectory(_dir);
        string corpus = Path.Combine(_dir, "corpus");
        Directory.CreateDirectory(Path.Combine(corpus, "pass-a"));
        string src = Path.Combine(_dir, "src.gz");
        var lines = new List<string> { "[" };
        foreach (var o in observed) lines.Add(ItemJson(o) + ",");
        lines.Add("{\"type\":\"property\",\"id\":\"P31\"},");
        lines.Add("]");
        WriteGz(src, lines);
        string t2Path = Path.Combine(corpus, "pass-a", "t2-endpoints.bin");
        WriteT2(t2Path, endpoints);

        var opts = new PassBOptions { SourcePath = src, CorpusRoot = corpus, LocalTestMode = true, T2Path = t2Path };
        PassBEvidence ev = PassB.Run(opts);

        Assert.Equal(conceptExpected.Length, ev.ConceptRows);
        Assert.Equal(expectedUnobserved, ev.UnobservedConceptTail);
        Assert.Equal(expectedLexical, ev.LexicalRows);
        Assert.Equal(instanceExpected.Length, ev.InstanceOfRows);
        Assert.Equal(subclassExpected.Length, ev.SubclassOfRows);

        // Published artifacts and evidence exist.
        string pub = Path.Combine(corpus, "pass-b");
        Assert.True(File.Exists(Path.Combine(pub, "concept.parquet")));
        Assert.True(File.Exists(Path.Combine(pub, "lexical_entry.parquet")));
        Assert.True(File.Exists(Path.Combine(pub, "instance_of.parquet")));
        Assert.True(File.Exists(Path.Combine(pub, "subclass_of.parquet")));
        Assert.True(File.Exists(Path.Combine(pub, "materialization.json")));
        Assert.Equal(Path.Combine(pub, "materialization.json"), ev.MaterializationPath);

        // Concept set is exactly T1 ∪ T2, and every flag matches expectations.
        var qids = ParquetRead.ReadLongs(Path.Combine(pub, "concept.parquet"), 0);
        var in1 = ParquetRead.ReadBools(Path.Combine(pub, "concept.parquet"), 1);
        var in2 = ParquetRead.ReadBools(Path.Combine(pub, "concept.parquet"), 2);
        var qidSet = qids.ToHashSet();
        Assert.Equal(conceptExpected.ToHashSet(), qidSet);
        for (int i = 0; i < qids.Length; i++)
        {
            Assert.Equal(t1.Contains(qids[i]), in1[i]);
            Assert.Equal(t2Expected.Contains(qids[i]), in2[i]);
        }

        // Unobserved T2 endpoint Q200 => InT1=false, InT2=true (not T1 even if hash would match).
        int idx200 = Array.IndexOf(qids, 200);
        Assert.True(idx200 >= 0);
        Assert.False(in1[idx200]);
        Assert.True(in2[idx200]);

        // Critical rule: Q164 hashes into the T1 bucket but is only a P279 endpoint
        // (no observed item record), so it must NOT become a T1 concept.
        Assert.True(CorpusHash.IsT1(164), "Q164 must hash into the T1 bucket for this test to be meaningful");
        Assert.False(observedIds.Contains(164));
        int idx164 = Array.IndexOf(qids, 164);
        Assert.True(idx164 >= 0);
        Assert.False(in1[idx164]);
        Assert.True(in2[idx164]);

        // T2-only concepts have no alias rows.
        var lexQ = ParquetRead.ReadLongs(Path.Combine(pub, "lexical_entry.parquet"), 0);
        var lexKind = ParquetRead.ReadStrings(Path.Combine(pub, "lexical_entry.parquet"), 2);
        for (int i = 0; i < lexQ.Length; i++)
            if (lexKind[i] == "alias")
                Assert.False(t2OnlyExpected.Contains(lexQ[i]));

        // Edges.
        var io = new (long S, long T)[ev.InstanceOfRows];
        var ioQ = ParquetRead.ReadLongs(Path.Combine(pub, "instance_of.parquet"), 0);
        var ioT = ParquetRead.ReadLongs(Path.Combine(pub, "instance_of.parquet"), 1);
        for (int i = 0; i < io.Length; i++) io[i] = (ioQ[i], ioT[i]);
        Assert.Equal(instanceExpected, io);
        Assert.All(ioQ, s => Assert.True(t1.Contains(s)));

        var so = new (long S, long T)[ev.SubclassOfRows];
        var soQ = ParquetRead.ReadLongs(Path.Combine(pub, "subclass_of.parquet"), 0);
        var soT = ParquetRead.ReadLongs(Path.Combine(pub, "subclass_of.parquet"), 1);
        for (int i = 0; i < so.Length; i++) so[i] = (soQ[i], soT[i]);
        Assert.Equal(subclassExpected, so);
        Assert.All(soQ.Concat(soT), q => Assert.Contains(q, qidSet));

        // No silent overwrite of an already published Pass B.
        Assert.Throws<InvalidDataException>(() => PassB.Run(opts));

        // Materialization evidence is valid JSON with schema identity.
        using var doc = JsonDocument.Parse(File.ReadAllText(ev.MaterializationPath));
        Assert.Equal("concept.parquet", doc.RootElement.GetProperty("artifacts").GetProperty("concept").GetProperty("relativePath").GetString());
    }

    [Fact]
    public void MissingP279Endpoint_FailsAndIsNotPublished()
    {
        string corpus = Path.Combine(_dir, "missing", "corpus");
        Directory.CreateDirectory(Path.Combine(corpus, "pass-a"));
        string src = Path.Combine(_dir, "missing", "src.gz");
        WriteGz(src, new[] { "[", ItemJson(new Rec(1, "one", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<long>(), new long[] { 999 })) + ",", "]" });
        string t2Path = Path.Combine(corpus, "pass-a", "t2-endpoints.bin");
        WriteT2(t2Path, new[] { 1L }); // subject present, target 999 absent

        var opts = new PassBOptions { SourcePath = src, CorpusRoot = corpus, LocalTestMode = true, T2Path = t2Path };
        Assert.Throws<InvalidDataException>(() => PassB.Run(opts));
        Assert.False(Directory.Exists(Path.Combine(corpus, "pass-b")), "pass-b must not be published");

        var staging = Directory.GetDirectories(corpus, "pass-b-staging-*").Single();
        string state = File.ReadAllText(Path.Combine(staging, "pass-b.state.json"));
        Assert.Contains("\"Failed\"", state);
    }

    [Fact]
    public void CorruptT2Artifact_FailsAndIsNotPublished()
    {
        string corpus = Path.Combine(_dir, "corrupt", "corpus");
        Directory.CreateDirectory(Path.Combine(corpus, "pass-a"));
        string src = Path.Combine(_dir, "corrupt", "src.gz");
        WriteGz(src, new[] { "[", "]" });
        string t2Path = Path.Combine(corpus, "pass-a", "t2-endpoints.bin");
        File.WriteAllBytes(t2Path, new byte[] { 1, 2, 3, 4, 5, 6, 7 }); // not a multiple of 8

        var opts = new PassBOptions { SourcePath = src, CorpusRoot = corpus, LocalTestMode = true, T2Path = t2Path };
        Assert.Throws<InvalidDataException>(() => PassB.Run(opts));
        Assert.False(Directory.Exists(Path.Combine(corpus, "pass-b")));
    }
}
