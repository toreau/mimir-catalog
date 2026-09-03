using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class ServingLoaderTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mimir-sl-" + Guid.NewGuid().ToString("N"));
        public string? IdentityManifestSha;

        public void Write(string serving, string expected)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "serving-probes.jsonl"), serving);
            File.WriteAllText(Path.Combine(Dir, "expected-results.jsonl"), expected);
            File.WriteAllText(Path.Combine(Dir, "workload.state.json"), "{\"state\":\"Complete\"}");
            var manifest = new System.Text.Json.Nodes.JsonObject
            {
                ["workload_id"] = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                ["corpus_id"] = "511adb9ebd066f1d4d344b80171902d5",
                ["files"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["serving-probes.jsonl"] = ServingWorkloadLoader.Sha256(Path.Combine(Dir, "serving-probes.jsonl")),
                    ["expected-results.jsonl"] = ServingWorkloadLoader.Sha256(Path.Combine(Dir, "expected-results.jsonl")),
                },
            };
            string manifestJson = manifest.ToJsonString();
            File.WriteAllText(Path.Combine(Dir, "manifest.json"), manifestJson);
            IdentityManifestSha = ServingWorkloadLoader.Sha256(Path.Combine(Dir, "manifest.json"));
        }

        public ServingWorkload Load()
        {
            var id = new ServingWorkloadLoader.Identity(IdentityManifestSha!,
                "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc",
                "511adb9ebd066f1d4d344b80171902d5",
                ServingWorkloadLoader.Sha256(Path.Combine(Dir, "serving-probes.jsonl")),
                ServingWorkloadLoader.Sha256(Path.Combine(Dir, "expected-results.jsonl")));
            return ServingWorkloadLoader.LoadFixture(Dir, id);
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static string P(string op, long seq, string stratum, bool measured, long? qid = null, string? lang = null, string? value = null)
    {
        var parts = new List<string> { $"\"op\":\"{op}\"", $"\"seq\":{seq}", $"\"stratum\":\"{stratum}\"", $"\"measured\":{measured.ToString().ToLowerInvariant()}" };
        if (qid != null) parts.Add($"\"qid\":{qid}");
        if (lang != null) parts.Add($"\"lang\":\"{lang}\"");
        if (value != null) parts.Add($"\"value\":\"{value}\"");
        return "{" + string.Join(",", parts) + "}";
    }

    private static string E(string op, long seq, bool measured, long card, string digest)
        => $"{{\"op\":\"{op}\",\"seq\":{seq},\"measured\":{measured.ToString().ToLowerInvariant()},\"cardinality\":{card},\"digest\":\"{digest}\"}}";

    private static string ShaOf(string s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Loader_ParsesShapes_AndTailMeasuredFalse()
    {
        using var f = new Fixture();
        f.Write(
            string.Join("\n", P("S1", 0, "Tail", false, qid: 1), P("S2", 1, "Miss", true, lang: "nb", value: "x")) + "\n",
            string.Join("\n", E("S1", 0, false, 1, ShaOf("a")), E("S2", 1, true, 0, ShaOf("b"))) + "\n");
        var w = f.Load();
        Assert.Equal(2, w.Probes.Count);
        Assert.False(w.Probes[0].Measured);
        Assert.True(w.Probes[1].Qid is null && w.Probes[1].Lang == "nb");
        Assert.Equal(2, w.Expected.Count);
    }

    [Theory]
    [InlineData("dupprobe")]
    [InlineData("dupexp")]
    [InlineData("keyset")]
    [InlineData("measured")]
    public void Loader_Rejects_InvalidPackage(string kind)
    {
        using var f = new Fixture();
        string serving = P("S1", 0, "T1Only", true, qid: 1) + "\n";
        string expected = E("S1", 0, true, 0, ShaOf("a")) + "\n";
        switch (kind)
        {
            case "dupprobe":
                serving = P("S1", 0, "T1Only", true, qid: 1) + "\n" + P("S1", 0, "T1Only", true, qid: 2) + "\n";
                break;
            case "dupexp":
                expected = E("S1", 0, true, 0, ShaOf("a")) + "\n" + E("S1", 0, true, 0, ShaOf("b")) + "\n";
                break;
            case "keyset":
                expected = E("S1", 7, true, 0, ShaOf("a")) + "\n";
                break;
            case "measured":
                expected = E("S1", 0, false, 0, ShaOf("a")) + "\n";
                break;
        }
        f.Write(serving, expected);
        Assert.Throws<InvalidDataException>(() => f.Load());
    }

    [Fact]
    public void Loader_Authoritative_RejectsWrongHashes()
    {
        using var f = new Fixture();
        f.Write(P("S1", 0, "T1Only", true, qid: 1) + "\n", E("S1", 0, true, 0, ShaOf("a")) + "\n");
        // Manifest hash is not the authoritative one.
        Assert.Throws<InvalidDataException>(() =>
            ServingWorkloadLoader.Load(f.Dir));
    }
}
