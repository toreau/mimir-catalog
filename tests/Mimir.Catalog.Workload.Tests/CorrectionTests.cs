using System.Text.Json;
using System.Text.Json.Nodes;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

/// <summary>
/// Corrective-round tests: tracked machine contract as executable source of
/// truth, canonical identity coverage, strict Pass-C/fixture preflight, the
/// Fanout51Plus census, G2 degree>=2 pool semantics and S3 tail exclusion.
/// </summary>
public class CorrectionTests
{
    private static string FindRepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, rel);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(rel);
    }

    private static WorkloadContractV1 ParseTracked()
    {
        string path = FindRepoFile(Path.Combine("benchmarks", "workload-contract-v1.json"));
        return WorkloadContractV1.Parse(File.ReadAllBytes(path));
    }

    private static string MutateJson(Action<JsonObject> edit)
    {
        string path = FindRepoFile(Path.Combine("benchmarks", "workload-contract-v1.json"));
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        return root.ToJsonString();
    }

    // ---- contract loader ----

    [Fact]
    public void TrackedContract_Parses_EqualsDefaults()
    {
        var parsed = ParseTracked();
        Assert.Equal(WorkloadContractV1.Default().CanonicalNormative(), parsed.CanonicalNormative());
        Assert.Equal(1, parsed.WorkloadContractVersion);
    }

    [Fact]
    public void ContractLoader_Rejects_UnknownField()
    {
        string json = MutateJson(o => o["bogusField"] = JsonValue.Create(1));
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ContractLoader_Rejects_MissingRequiredField()
    {
        string json = MutateJson(o => o.Remove("selectionDomain"));
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ContractLoader_Rejects_WrongVersion()
    {
        string json = MutateJson(o => o["workloadContractVersion"] = JsonValue.Create(2));
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ContractLoader_Rejects_DuplicateStratum()
    {
        string json = MutateJson(o =>
        {
            var strata = o["strata"]!.AsArray();
            strata.Add(JsonNode.Parse("{\"operation\":\"S1\",\"stratum\":\"T1Only\",\"selectionMode\":\"sha256-ranked-sample\",\"measuredCount\":4000}"));
        });
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ContractLoader_Rejects_UnknownSelectionMode()
    {
        string json = MutateJson(o =>
        {
            var strata = o["strata"]!.AsArray();
            ((JsonObject)strata[0]!)["selectionMode"] = JsonValue.Create("mystery-mode");
        });
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ContractLoader_Rejects_ReportP999True()
    {
        string json = MutateJson(o => o["reportP999"] = JsonValue.Create(true));
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    // ---- identity coverage ----

    private static readonly (string Name, string Sha)[] IdArtifacts = { ("concept.parquet", "a".PadRight(64, 'a')) };

    private static string Id(WorkloadContractV1 c) => WorkloadIdentity.Compute(c, "c", IdArtifacts, "f".PadRight(64, 'f'));

    [Fact]
    public void Identity_SensitiveTo_NormativeFields()
    {
        string baseId = Id(WorkloadContractV1.Default());
        Assert.Equal(baseId, Id(WorkloadContractV1.Default()));
        Assert.NotEqual(baseId, Id(new WorkloadContractV1 { MissLanguage = "en" }));
        Assert.NotEqual(baseId, Id(new WorkloadContractV1 { FullPassWarmup = false }));
        Assert.NotEqual(baseId, Id(new WorkloadContractV1 { PercentileAlgorithm = "some-other-v1" }));

        var altered = new WorkloadContractV1
        {
            Strata = WorkloadContractV1.DefaultStrata().Select(s =>
                s is { Operation: "S2", Stratum: "Fanout51Plus" }
                    ? s with { MeasuredCount = 380, ExpectedEligibleCount = 380 } // identical semantics
                    : s).ToList(),
        };
        Assert.Equal(baseId, Id(altered));

        var censusChanged = new WorkloadContractV1
        {
            Strata = WorkloadContractV1.DefaultStrata().Select(s =>
                s is { Operation: "S2", Stratum: "Fanout51Plus" }
                    ? s with { MeasuredCount = 381, ExpectedEligibleCount = 381 }
                    : s).ToList(),
        };
        Assert.NotEqual(baseId, Id(censusChanged));
    }

    // ---- preflight ----

    private static string MakeCorpus(string variant, bool omitFixtureIdentity = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "mimir-pf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pass-a"));
        Directory.CreateDirectory(Path.Combine(root, "pass-b"));
        Directory.CreateDirectory(Path.Combine(root, "pass-c"));
        File.WriteAllBytes(Path.Combine(root, "pass-a", "t2-endpoints.bin"), System.Text.Encoding.UTF8.GetBytes("T2"));
        File.WriteAllBytes(Path.Combine(root, "pass-b", "concept.parquet"), System.Text.Encoding.UTF8.GetBytes("C"));
        File.WriteAllBytes(Path.Combine(root, "pass-b", "lexical_entry.parquet"), System.Text.Encoding.UTF8.GetBytes("L"));
        File.WriteAllBytes(Path.Combine(root, "pass-b", "instance_of.parquet"), System.Text.Encoding.UTF8.GetBytes("I"));
        File.WriteAllBytes(Path.Combine(root, "pass-b", "subclass_of.parquet"), System.Text.Encoding.UTF8.GetBytes("S"));
        string fixture = Path.Combine(root, "phase0-anchors-v1.json");
        File.WriteAllText(fixture, "{\"sets\":{}}");

        var inputs = new JsonObject
        {
            ["t2"] = new JsonObject { ["count"] = 1, ["bytes"] = 2, ["sha256"] = Canon.Sha256Hex(File.ReadAllBytes(Path.Combine(root, "pass-a", "t2-endpoints.bin"))) },
            ["parquetArtifacts"] = new JsonObject
            {
                ["concept"] = ShaOf(root, "concept"),
                ["lexical_entry"] = ShaOf(root, "lexical_entry"),
                ["instance_of"] = ShaOf(root, "instance_of"),
                ["subclass_of"] = ShaOf(root, "subclass_of"),
            },
        };
        if (!omitFixtureIdentity)
            inputs["phase0Fixture"] = new JsonObject { ["path"] = fixture, ["sha256"] = Canon.Sha256Hex(File.ReadAllBytes(fixture)) };

        var val = new JsonObject
        {
            ["completed"] = true,
            ["verdict"] = "GO",
            ["failedGates"] = new JsonArray(),
            ["inputs"] = inputs,
        };
        File.WriteAllText(Path.Combine(root, "pass-c", "validation.json"), val.ToJsonString());

        string state = variant switch
        {
            "missing" => "{}",
            "running" => "{\"state\":\"Running\"}",
            "malformed" => "{not-json",
            _ => "{\"state\":\"Complete\"}",
        };
        File.WriteAllText(Path.Combine(root, "pass-c", "validation.state.json"), state);

        if (variant == "fixture-modified")
            File.WriteAllText(fixture, "{\"sets\":{},\"changed\":true}"); // differs from recorded SHA

        return root;
    }

    private static JsonObject ShaOf(string root, string key)
    {
        string name = key == "concept" || key == "lexical_entry" || key == "instance_of" || key == "subclass_of"
            ? key + ".parquet" : key;
        return new JsonObject { ["bytes"] = 1, ["sha256"] = Canon.Sha256Hex(File.ReadAllBytes(Path.Combine(root, "pass-b", name))) };
    }

    [Fact]
    public void Preflight_Positive_Passes()
    {
        string root = MakeCorpus("ok");
        try
        {
            var o = WorkloadRun.CheckPreflight(root, Path.Combine(root, "phase0-anchors-v1.json"));
            Assert.True(o.Ok);
            Assert.Equal(5, o.ArtifactShas.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preflight_Rejects_MissingState()
    {
        string root = MakeCorpus("missing");
        try
        {
            var o = WorkloadRun.CheckPreflight(root, Path.Combine(root, "phase0-anchors-v1.json"));
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, r => r.Contains("state"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preflight_Rejects_RunningState()
    {
        string root = MakeCorpus("running");
        try
        {
            var o = WorkloadRun.CheckPreflight(root, Path.Combine(root, "phase0-anchors-v1.json"));
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, r => r.Contains("Complete"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preflight_Rejects_MalformedStateJson()
    {
        string root = MakeCorpus("malformed");
        try
        {
            var o = WorkloadRun.CheckPreflight(root, Path.Combine(root, "phase0-anchors-v1.json"));
            Assert.False(o.Ok);
            Assert.NotEmpty(o.Reasons);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preflight_Rejects_FixtureModified()
    {
        string root = MakeCorpus("fixture-modified");
        try
        {
            var o = WorkloadRun.CheckPreflight(root, Path.Combine(root, "phase0-anchors-v1.json"));
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, r => r.Contains("fixture SHA mismatch"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preflight_Rejects_MissingFixtureIdentity()
    {
        string root = MakeCorpus("ok", omitFixtureIdentity: true);
        try
        {
            var o = WorkloadRun.CheckPreflight(root, Path.Combine(root, "phase0-anchors-v1.json"));
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, r => r.Contains("phase0Fixture"));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- census semantics ----

    private static WorkloadBuild.Result BuildWorld(SyntheticWorld.Tables w)
        => WorkloadBuild.Build(WorkloadContractV1.Default(), "synth-1", w.Concept, w.Lexical, w.Instance, w.Subclass, () => w.Rows, w.FixturePath);

    private static List<JsonElement> ParseLines(byte[] bytes)
    {
        var list = new List<JsonElement>();
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            list.Add(JsonDocument.Parse(line).RootElement.Clone());
        return list;
    }

    [Fact]
    public void Census380_AllIncluded_ExactlyOnce_Deterministic()
    {
        var w = SyntheticWorld.Build(380);
        try
        {
            var r1 = BuildWorld(w);
            var r2 = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Go, r1.Verdict);
            var lines = ParseLines(r1.ServingLines!).Where(l => l.GetProperty("op").GetString() == "S2"
                && l.GetProperty("stratum").GetString() == "Fanout51Plus" && l.GetProperty("measured").GetBoolean()).ToList();
            Assert.Equal(380, lines.Count);
            var keys = lines.Select(l => l.GetProperty("lang").GetString() + "\u001f" + l.GetProperty("value").GetString()).ToList();
            Assert.Equal(380, keys.Distinct().Count());
            Assert.Equal(keys, ParseLines(r2.ServingLines!).Where(l => l.GetProperty("op").GetString() == "S2"
                && l.GetProperty("stratum").GetString() == "Fanout51Plus" && l.GetProperty("measured").GetBoolean())
                .Select(l => l.GetProperty("lang").GetString() + "\u001f" + l.GetProperty("value").GetString()).ToList());
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Theory]
    [InlineData(379)]
    [InlineData(381)]
    public void Census_Holds_WhenPopulationMismatch(int hi)
    {
        var w = SyntheticWorld.Build(hi);
        try
        {
            var r = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Hold, r.Verdict);
            Assert.Contains(r.Reasons, reason => reason.StartsWith("S2/Fanout51Plus: census mismatch"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void S3_NoLexical_MeasuredPool_ExcludesTail()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Go, r.Verdict);
            var qids = ParseLines(r.ServingLines!).Where(l => l.GetProperty("op").GetString() == "S3"
                && l.GetProperty("stratum").GetString() == "ConceptNoLexical" && l.GetProperty("measured").GetBoolean())
                .Select(l => l.GetProperty("qid").GetInt64()).ToHashSet();
            Assert.Equal(1000, qids.Count);
            Assert.Empty(qids.Intersect(w.Concept.TailQids));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void G2_Degree2PlusPool_IncludesHighDegree()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Go, r.Verdict);
            long g2 = r.PoolCardinalities["G2/P31Degree2Plus"];
            long ordinary = r.PoolCardinalities["S4/Degree2Plus"];
            long high = r.PoolCardinalities["S4/HighDegree"];
            Assert.Equal(ordinary + high, g2); // all degree>=2, high-degree NOT excluded
            Assert.True(g2 > ordinary);
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void LoadedContract_NormativeChange_ChangesGenerationAndIdentity()
    {
        var w = SyntheticWorld.Build();
        try
        {
            string json = MutateJson(o =>
            {
                var strata = o["strata"]!.AsArray();
                foreach (var node in strata)
                {
                    var s = node!.AsObject();
                    if (s["operation"]?.GetValue<string>() == "S2" && s["stratum"]?.GetValue<string>() == "Fanout1")
                        s["measuredCount"] = JsonValue.Create(7);
                }
            });
            var contract = WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(json));
            Assert.NotEqual(WorkloadContractV1.Default().CanonicalNormative(), contract.CanonicalNormative());

            var r = WorkloadBuild.Build(contract, "synth-1", w.Concept, w.Lexical, w.Instance, w.Subclass, () => w.Rows, w.FixturePath);
            Assert.Equal(WorkloadBuild.Go, r.Verdict);
            var group = ParseLines(r.ServingLines!).Count(l => l.GetProperty("op").GetString() == "S2"
                && l.GetProperty("stratum").GetString() == "Fanout1" && l.GetProperty("measured").GetBoolean());
            Assert.Equal(7, group);
        }
        finally { SyntheticWorld.Cleanup(w); }
    }
}
