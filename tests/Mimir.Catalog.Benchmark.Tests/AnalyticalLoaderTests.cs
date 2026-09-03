using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class AnalyticalLoaderTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mimir-al-" + Guid.NewGuid().ToString("N"));

        public void Write(string analyticalLines)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "analytical-expected.jsonl"), analyticalLines);
            File.WriteAllText(Path.Combine(Dir, "workload.state.json"), "{\"state\":\"Complete\"}");
            var manifest = new System.Text.Json.Nodes.JsonObject
            {
                ["workload_id"] = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                ["corpus_id"] = "511adb9ebd066f1d4d344b80171902d5",
                ["files"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["analytical-expected.jsonl"] = AnalyticalWorkloadLoader.Sha256(Path.Combine(Dir, "analytical-expected.jsonl")),
                },
            };
            string manifestJson = manifest.ToJsonString();
            File.WriteAllText(Path.Combine(Dir, "manifest.json"), manifestJson);
            ManifestSha = AnalyticalWorkloadLoader.Sha256(Path.Combine(Dir, "manifest.json"));
        }

        public string? ManifestSha { get; private set; }

        public AnalyticalWorkload Load()
        {
            var id = new AnalyticalWorkloadLoader.Identity(ManifestSha!,
                "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                "511adb9ebd066f1d4d344b80171902d5",
                AnalyticalWorkloadLoader.Sha256(Path.Combine(Dir, "analytical-expected.jsonl")));
            return AnalyticalWorkloadLoader.LoadFixture(Dir, id);
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static string Line(string op, long card, string digest) => $"{{\"op\":\"{op}\",\"cardinality\":{card},\"digest\":\"{digest}\"}}";

    private static string Std()
    {
        var d = new[] { "a", "b", "c", "d", "e", "f", "0" };
        var ops = new[] { "A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf", "A2", "A3", "A4" };
        return string.Join("\n", ops.Select((o, i) => Line(o, i + 1, d[i].PadRight(64, d[i][0]))))
            + "\n" + Line("A5", 9, "9".PadRight(64, '9'));
    }

    [Fact]
    public void Loader_SelectsA1ThroughA4_AndIgnoresA5()
    {
        using var f = new Fixture();
        f.Write(Std());
        var w = f.Load();
        Assert.Equal(7, w.Expected.Count);
        Assert.Equal(4, w.Expected.Keys.Count(o => o.StartsWith("A1-", StringComparison.Ordinal)));
        Assert.Contains("A2", w.Expected.Keys);
        Assert.Contains("A3", w.Expected.Keys);
        Assert.Contains("A4", w.Expected.Keys);
        Assert.DoesNotContain("A5", w.Expected.Keys);
    }

    [Fact]
    public void Loader_Rejects_MissingDuplicateExtraAndBadIdentity()
    {
        using var f = new Fixture();
        f.Write(string.Join("\n", Std().Split('\n').Skip(1)));
        Assert.Throws<InvalidDataException>(() => f.Load());

        using var f2 = new Fixture();
        f2.Write(Std() + "\n" + Line("A1-Concept", 1, "e"));
        Assert.Throws<InvalidDataException>(() => f2.Load());

        using var f3 = new Fixture();
        f3.Write(Std() + "\n" + Line("A2", 5, "f"));
        Assert.Throws<InvalidDataException>(() => f3.Load()); // duplicate A2

        using var f4 = new Fixture();
        f4.Write(Std());
        Assert.Throws<InvalidDataException>(() => AnalyticalWorkloadLoader.Load(f4.Dir));
    }
}
