using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class GraphLoaderTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mimir-gl-" + Guid.NewGuid().ToString("N"));

        public void Write(string graphLines, string expectedLines, string? extraGraphFileLines = null)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "graph-probes.jsonl"), graphLines + (extraGraphFileLines ?? ""));
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

        public GraphWorkload Load()
        {
            var id = new GraphWorkloadLoader.Identity(ManifestSha!,
                "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                "511adb9ebd066f1d4d344b80171902d5",
                GraphWorkloadLoader.Sha256(Path.Combine(Dir, "graph-probes.jsonl")),
                GraphWorkloadLoader.Sha256(Path.Combine(Dir, "expected-results.jsonl")));
            return GraphWorkloadLoader.LoadFixture(Dir, id);
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static string Probe(long seq, string stratum, long start, bool measured = true)
        => $"{{\"op\":\"G1\",\"seq\":{seq},\"stratum\":\"{stratum}\",\"measured\":{measured.ToString().ToLowerInvariant()},\"start\":{start}}}";

    private static string Expected(long seq, bool measured = true, bool omitVisited = false, long visited = 1, string digest = "d")
        => $"{{\"op\":\"G1\",\"seq\":{seq},\"measured\":{measured.ToString().ToLowerInvariant()},\"cardinality\":0{(omitVisited ? "" : $",\"visited\":{visited}")},\"digest\":\"{digest}\"}}";

    private static string StdProbes()
    {
        var lines = new List<string>();
        for (int i = 0; i < 500; i++)
            lines.Add(Probe(i, i < 250 ? "Degree1" : "Degree2Plus", 1000 + i));
        return string.Join("\n", lines);
    }

    private static string StdExpected()
    {
        var lines = new List<string>();
        for (int i = 0; i < 500; i++) lines.Add(Expected(i));
        return string.Join("\n", lines);
    }

    [Fact]
    public void Loader_ValidFixture_500Probes()
    {
        using var f = new Fixture();
        f.Write(StdProbes(), StdExpected());
        var w = f.Load();
        Assert.Equal(500, w.Probes.Count);
        Assert.Equal(250, w.Probes.Count(p => p.Stratum == "Degree1"));
        Assert.Equal(250, w.Probes.Count(p => p.Stratum == "Degree2Plus"));
        Assert.All(w.Probes, p => Assert.True(p.Measured));
        Assert.Equal(w.Probes.Select(p => p.Seq).OrderBy(x => x), Enumerable.Range(0, 500).Select(i => (long)i));
        Assert.Equal(500, w.Expected.Count);
    }

    [Fact]
    public void Loader_Rejects_DuplicateProbeAndExpectedAndMismatch()
    {
        using var f = new Fixture();
        f.Write(StdProbes() + "\n" + Probe(0, "Degree1", 1), StdExpected());
        Assert.Throws<InvalidDataException>(() => f.Load());

        using var f2 = new Fixture();
        f2.Write(StdProbes(), StdExpected() + "\n" + Expected(0));
        Assert.Throws<InvalidDataException>(() => f2.Load());

        // key mismatch: replace last expected seq
        using var f3 = new Fixture();
        var exp = new List<string>();
        for (int i = 0; i < 499; i++) exp.Add(Expected(i));
        exp.Add(Expected(777));
        f3.Write(StdProbes(), string.Join("\n", exp));
        Assert.Throws<InvalidDataException>(() => f3.Load());
    }

    [Fact]
    public void Loader_Rejects_MeasuredFalseProbe_AndMissingVisited()
    {
        using var f = new Fixture();
        var probes = StdProbes().Split('\n').ToList();
        probes[0] = Probe(0, "Degree1", 1000, measured: false);
        f.Write(string.Join("\n", probes), StdExpected());
        Assert.Throws<InvalidDataException>(() => f.Load());

        using var f2 = new Fixture();
        var exp = new List<string>();
        for (int i = 0; i < 500; i++) exp.Add(Expected(i, omitVisited: i == 0));
        f2.Write(StdProbes(), string.Join("\n", exp));
        Assert.Throws<InvalidDataException>(() => f2.Load());
    }

    [Fact]
    public void Loader_G2RowsExcluded_FromG1Matching()
    {
        using var f = new Fixture();
        string g2Probe = "\n{\"op\":\"G2\",\"seq\":500,\"stratum\":\"Batch\",\"measured\":true,\"concepts\":200}\n";
        string g2Expected = "{\"op\":\"G2\",\"seq\":500,\"kind\":\"PerInput\",\"measured\":false,\"item\":0,\"qid\":1,\"source_stratum\":\"P31Degree1\",\"cardinality\":1,\"digest\":\"x\"}\n"
            + "{\"op\":\"G2\",\"seq\":500,\"kind\":\"Batch\",\"measured\":true,\"cardinality\":200,\"digest\":\"y\"}\n";
        f.Write(StdProbes(), StdExpected() + "\n" + g2Expected, extraGraphFileLines: g2Probe);
        var w = f.Load();
        Assert.Equal(500, w.Probes.Count);
        Assert.DoesNotContain(w.Expected.Keys, k => k.Op == "G2");
    }

    [Fact]
    public void Loader_Authoritative_RejectsWrongHashes()
    {
        using var f = new Fixture();
        f.Write(StdProbes(), StdExpected());
        Assert.Throws<InvalidDataException>(() => GraphWorkloadLoader.Load(f.Dir));
    }

    [Fact]
    public void PublicApi_NoSyntheticSwitch()
    {
        var methods = typeof(GraphWorkloadLoader).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == "Load").ToArray();
        Assert.NotEmpty(methods);
        foreach (var m in methods)
            Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(bool));
    }
}
