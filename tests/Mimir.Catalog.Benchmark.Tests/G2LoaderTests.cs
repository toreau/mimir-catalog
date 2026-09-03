using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2LoaderTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mimir-g2l-" + Guid.NewGuid().ToString("N"));

        public void Write(string graphLines, string expectedLines)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "graph-probes.jsonl"), graphLines);
            File.WriteAllText(Path.Combine(Dir, "expected-results.jsonl"), expectedLines);
            File.WriteAllText(Path.Combine(Dir, "workload.state.json"), "{\"state\":\"Complete\"}");
            var manifest = new System.Text.Json.Nodes.JsonObject
            {
                ["workload_id"] = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                ["corpus_id"] = "511adb9ebd066f1d4d344b80171902d5",
                ["files"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["graph-probes.jsonl"] = GraphWorkloadLoader.Sha256(Path.Combine(Dir, "graph-probes.jsonl")),
                    ["expected-results.jsonl"] = GraphWorkloadLoader.Sha256(Path.Combine(Dir, "expected-results.jsonl")),
                },
            };
            string manifestJson = manifest.ToJsonString();
            File.WriteAllText(Path.Combine(Dir, "manifest.json"), manifestJson);
            ManifestSha = GraphWorkloadLoader.Sha256(Path.Combine(Dir, "manifest.json"));
        }

        public string? ManifestSha { get; private set; }

        public G2Workload Load()
        {
            var id = new GraphWorkloadLoader.Identity(ManifestSha!,
                "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                "511adb9ebd066f1d4d344b80171902d5",
                GraphWorkloadLoader.Sha256(Path.Combine(Dir, "graph-probes.jsonl")),
                GraphWorkloadLoader.Sha256(Path.Combine(Dir, "expected-results.jsonl")));
            return GraphWorkloadLoader.LoadG2Fixture(Dir, id);
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static string BatchProbe(long[] qids)
    {
        var items = qids.Select((q, i) => $"{{\"qid\":{q},\"source_stratum\":\"{(i < 100 ? "P31Degree1" : "P31Degree2Plus")}\"}}");
        return $"{{\"op\":\"G2\",\"stratum\":\"Batch\",\"seq\":500,\"measured\":true,\"concepts\":[{string.Join(",", items)}]}}";
    }

    private static string PerInputLine(int item, long qid, string source)
        => $"{{\"op\":\"G2\",\"seq\":500,\"kind\":\"PerInput\",\"measured\":false,\"item\":{item},\"qid\":{qid},\"source_stratum\":\"{source}\",\"cardinality\":1,\"digest\":\"d\"}}";

    private static string BatchLine()
        => "{\"op\":\"G2\",\"seq\":500,\"kind\":\"Batch\",\"measured\":true,\"cardinality\":200,\"digest\":\"b\"}";

    private static long[] StdQids()
    {
        var q = new long[200];
        for (int i = 0; i < 200; i++) q[i] = 1_000_000 + i;
        return q;
    }

    private static string StdExpected(long[] qids)
    {
        var lines = new List<string>();
        for (int i = 0; i < qids.Length; i++)
            lines.Add(PerInputLine(i, qids[i], i < 100 ? "P31Degree1" : "P31Degree2Plus"));
        lines.Add(BatchLine());
        return string.Join("\n", lines);
    }

    [Fact]
    public void LoaderG2_ValidFixture()
    {
        using var f = new Fixture();
        var qids = StdQids();
        f.Write(BatchProbe(qids), StdExpected(qids));
        var w = f.Load();
        Assert.Equal(200, w.Concepts.Count);
        Assert.Equal(100, w.Concepts.Count(c => c.SourceStratum == "P31Degree1"));
        Assert.Equal(200, w.PerInput.Count);
        Assert.Equal(200, w.Batch.Cardinality);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(i, w.PerInput[i].Item);
            Assert.Equal(w.Concepts[i].Qid, w.PerInput[i].Qid);
            Assert.Equal(w.Concepts[i].SourceStratum, w.PerInput[i].SourceStratum);
        }
    }

    [Fact]
    public void LoaderG2_Rejects_PositionalMismatch()
    {
        using var f = new Fixture();
        var qids = StdQids();
        var expected = StdExpected(qids).Split('\n').ToList();
        expected[0] = PerInputLine(0, qids[1], "P31Degree1"); // swapped qid vs position 0
        f.Write(BatchProbe(qids), string.Join("\n", expected));
        Assert.Throws<InvalidDataException>(() => f.Load());
    }

    [Fact]
    public void LoaderG2_Rejects_CountAndStrata()
    {
        using var f = new Fixture();
        f.Write(BatchProbe(StdQids().Take(199).ToArray()), StdExpected(StdQids()));
        Assert.Throws<InvalidDataException>(() => f.Load());

        using var f2 = new Fixture();
        var qids = StdQids();
        f2.Write(BatchProbe(qids), StdExpected(qids).Replace("P31Degree2Plus", "P31Degree1"));
        Assert.Throws<InvalidDataException>(() => f2.Load());
    }

    [Fact]
    public void LoaderG2_Rejects_MissingPerInput_AndDuplicateBatch()
    {
        using var f = new Fixture();
        var qids = StdQids();
        var expected = StdExpected(qids).Split('\n').ToList();
        expected.RemoveAt(0); // one PerInput missing
        f.Write(BatchProbe(qids), string.Join("\n", expected));
        Assert.Throws<InvalidDataException>(() => f.Load());

        using var f2 = new Fixture();
        f2.Write(BatchProbe(qids), StdExpected(qids) + "\n" + BatchLine());
        Assert.Throws<InvalidDataException>(() => f2.Load());
    }

    [Fact]
    public void LoaderG2_Rejects_MeasuredFlags()
    {
        using var f = new Fixture();
        var qids = StdQids();
        string bad = StdExpected(qids).Replace("\"measured\":false", "\"measured\":true").Replace("\"measured\":true", "\"measured\":false");
        f.Write(BatchProbe(qids), bad);
        Assert.Throws<InvalidDataException>(() => f.Load());
    }

    [Fact]
    public void LoaderG2_Authoritative_RejectsWrongHashes()
    {
        using var f = new Fixture();
        f.Write(BatchProbe(StdQids()), StdExpected(StdQids()));
        Assert.Throws<InvalidDataException>(() => GraphWorkloadLoader.LoadG2(f.Dir));
    }
}
