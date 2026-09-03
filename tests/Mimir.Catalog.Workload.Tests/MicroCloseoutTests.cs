using System.Text.Json;
using System.Text.Json.Nodes;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

/// <summary>
/// Micro-closeout regression tests: G1/G2 artifact integrity, package gate,
/// strict contract semantic gates and provenance-bearing deterministic
/// manifest.
/// </summary>
public class MicroCloseoutTests
{
    private static string RepoRel(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(rel);
    }

    private static JsonObject TrackedRoot() => JsonNode.Parse(File.ReadAllText(RepoRel(Path.Combine("benchmarks", "workload-contract-v1.json"))))!.AsObject();

    private static WorkloadContractV1 ParseMutated(Func<JsonObject, JsonObject> edit)
    {
        var root = TrackedRoot();
        edit(root);
        return WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void ExpectReject(Action<JsonObject> edit)
    {
        Assert.Throws<InvalidDataException>(() =>
        {
            var root = TrackedRoot();
            edit(root);
            WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
        });
    }

    private static (string op, string stratum, bool measured, long seq)[] Lines(byte[] bytes)
    {
        var list = new List<(string, string, bool, long)>();
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            list.Add((e.GetProperty("op").GetString()!, e.TryGetProperty("stratum", out var st) ? st.GetString() ?? "" : "",
                e.TryGetProperty("measured", out var m) && m.GetBoolean(), e.GetProperty("seq").GetInt64()));
        }
        return list.ToArray();
    }

    private static WorkloadBuild.Result BuildWorld(SyntheticWorld.Tables w)
        => WorkloadBuild.Build(WorkloadContractV1.Default(), "synth-1", w.Concept, w.Lexical, w.Instance, w.Subclass, () => w.Rows, w.FixturePath);

    // ---- G1 cross-reference ----
    [Fact]
    public void G1_GraphExpected_OneToOne_Seq0To499()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Go, r.Verdict);
            var graph = Lines(r.GraphLines!).Where(l => l.op == "G1").Select(l => l.seq).ToArray();
            var expected = Lines(r.ExpectedLines!).Where(l => l.op == "G1").Select(l => l.seq).ToArray();
            Assert.Equal(500, graph.Length);
            Assert.Equal(500, expected.Length);
            Assert.Equal(graph.Distinct().Count(), graph.Length);
            Assert.Equal(expected.Distinct().Count(), expected.Length);
            Assert.Equal(Enumerable.Range(0, 500).Select(i => (long)i), graph.OrderBy(x => x));
            Assert.Equal(graph.OrderBy(x => x), expected.OrderBy(x => x));
            Assert.Equal(graph.ToHashSet(), expected.ToHashSet());
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    // ---- G2 materialization ----
    [Fact]
    public void G2_SerializedInputs_CompleteAndDeterministic()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r1 = BuildWorld(w);
            var r2 = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Go, r1.Verdict);
            var batch1 = ParseBatch(r1.GraphLines!);
            var batch2 = ParseBatch(r2.GraphLines!);
            Assert.Equal(200, batch1.Count);
            Assert.Equal(batch1, batch2);
            Assert.Equal(batch1.Select(b => b.qid).Distinct().Count(), 200);
            Assert.Equal(100, batch1.Count(b => b.source == "P31Degree1"));
            Assert.Equal(100, batch1.Count(b => b.source == "P31Degree2Plus"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    private static List<(long qid, string source)> ParseBatch(byte[] graphBytes)
    {
        foreach (var line in System.Text.Encoding.UTF8.GetString(graphBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.GetProperty("op").GetString() == "G2")
            {
                var list = new List<(long, string)>();
                foreach (var c in e.GetProperty("concepts").EnumerateArray())
                    list.Add((c.GetProperty("qid").GetInt64(), c.GetProperty("source_stratum").GetString()!));
                return list;
            }
        }
        throw new InvalidDataException("no G2 batch in graph file");
    }

    [Fact]
    public void G2_Expected_PerInputAndBatch()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var qids = ParseBatch(r.GraphLines!).Select(b => b.qid).ToArray();
            var expected = System.Text.Encoding.UTF8.GetString(r.ExpectedLines!).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("op").GetString() == "G2").ToArray();
            var perInput = expected.Where(e => e.GetProperty("kind").GetString() == "PerInput").ToArray();
            var batch = expected.Where(e => e.GetProperty("kind").GetString() == "Batch").ToArray();
            Assert.Single(batch);
            Assert.True(batch[0].GetProperty("measured").GetBoolean());
            Assert.Equal(200, perInput.Length);
            Assert.Equal(perInput.Select(e => e.GetProperty("qid").GetInt64()).OrderBy(x => x), qids.OrderBy(x => x));
            Assert.Equal(perInput.Select(e => e.GetProperty("item").GetInt64()).OrderBy(x => x), Enumerable.Range(0, 200).Select(i => (long)i).OrderBy(x => x));
            Assert.All(perInput, e => Assert.False(e.GetProperty("measured").GetBoolean()));
            Assert.All(perInput, e => Assert.Equal(64, e.GetProperty("digest").GetString()!.Length));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    // ---- package gate ----
    [Fact]
    public void PackageValidator_Accept_WellFormedPackage()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var o = WorkloadPackageValidator.Validate(WorkloadContractV1.Default(), r.ServingLines!, r.GraphLines!, r.ExpectedLines!, r.AnalyticalLines!);
            Assert.True(o.Ok, string.Join(";", o.Reasons));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void PackageValidator_Rejects_TamperedG1Seq()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            // Corrupt the first expected G1 seq from 0 to 9999.
            string text = System.Text.Encoding.UTF8.GetString(r.ExpectedLines!);
            int idx = text.IndexOf("\"op\":\"G1\"", StringComparison.Ordinal);
            text = text.Remove(idx, 0); // keep text; patch seq below is complex -> rebuild tampered via targeted replace of first G1 line
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                if (doc.RootElement.GetProperty("op").GetString() == "G1")
                {
                    var node = JsonNode.Parse(lines[i])!.AsObject();
                    node["seq"] = 9999;
                    lines[i] = node.ToJsonString();
                    break;
                }
            }
            byte[] tampered = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
            var o = WorkloadPackageValidator.Validate(WorkloadContractV1.Default(), r.ServingLines!, r.GraphLines!, tampered, r.AnalyticalLines!);
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, x => x.Contains("G1"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    // ---- contract semantic gates ----
    [Fact]
    public void Contract_Rejects_UnsupportedSemantics()
    {
        ExpectReject(o => o["lexicalKeySemantics"] = JsonValue.Create("fuzzy"));
        ExpectReject(o => o["orderingAlgorithm"] = JsonValue.Create("timestamp"));
        ExpectReject(o => o["orderingInterleave"] = JsonValue.Create("random"));
        ExpectReject(o => o["fullPassWarmup"] = JsonValue.Create(false));
    }

    [Theory]
    [InlineData("S1", "Unknown")]
    [InlineData("S2", "FanoutMystery")]
    [InlineData("S4", "OtherDegree")]
    [InlineData("G1", "Unknown")]
    public void Contract_Rejects_UnknownOperationStratum(string op, string stratum)
    {
        ExpectReject(o =>
        {
            var strata = o["strata"]!.AsArray();
            ((JsonObject)strata[0]!)["operation"] = JsonValue.Create(op);
            ((JsonObject)strata[0]!)["stratum"] = JsonValue.Create(stratum);
        });
    }

    [Fact]
    public void Contract_Rejects_UnknownG2FieldAndStratum()
    {
        ExpectReject(o => ((JsonObject)o["g2Strata"]!.AsArray()[0]!)["bogus"] = JsonValue.Create(1));
        ExpectReject(o => ((JsonObject)o["g2Strata"]!.AsArray()[0]!)["stratum"] = JsonValue.Create("P31DegreeX"));
    }

    [Fact]
    public void Contract_Rejects_MissingAndDuplicateG2Stratum()
    {
        ExpectReject(o => o["g2Strata"] = JsonNode.Parse("[{\"stratum\":\"P31Degree1\",\"count\":200}]"));
        ExpectReject(o => o["g2Strata"] = JsonNode.Parse(
            "[{\"stratum\":\"P31Degree1\",\"count\":100},{\"stratum\":\"P31Degree1\",\"count\":100}]"));
    }

    [Theory]
    [InlineData("Fanout1", "fanoutMin", 2, false)]      // first min != 1
    [InlineData("Fanout2To5", "fanoutMin", 3, false)]    // gap
    [InlineData("Fanout6To50", "fanoutMax", 0, true)]    // non-final unbounded
    [InlineData("Fanout51Plus", "fanoutMax", 100, false)] // final bounded
    public void Contract_Rejects_InvalidFanoutPartition(string stratum, string field, long value, bool setNull)
    {
        ExpectReject(o =>
        {
            foreach (var node in o["strata"]!.AsArray())
            {
                var s = node!.AsObject();
                if (s["stratum"]?.GetValue<string>() == stratum)
                {
                    if (setNull) s[field] = null;
                    else s[field] = JsonValue.Create(value);
                }
            }
        });
    }

    [Fact]
    public void Tail_ContractDriven_CensusMismatchHolds()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var c = new WorkloadContractV1
            {
                CorrectnessOnly = new List<WorkloadContractV1.StratumDef>
                {
                    new("S1", "Tail", WorkloadContractV1.SelectionModeAll, 21, null, null, 21),
                },
            };
            var r = WorkloadBuild.Build(c, "synth-1", w.Concept, w.Lexical, w.Instance, w.Subclass, () => w.Rows, w.FixturePath);
            Assert.Equal(WorkloadBuild.Hold, r.Verdict);
            Assert.Contains(r.Reasons, x => x.Contains("S1/Tail census mismatch"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void Tail_AllEmittedOnce_MeasuredFalse()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var tail = Lines(r.ServingLines!).Where(l => l.op == "S1" && l.stratum == "Tail").ToArray();
            Assert.Equal(20, tail.Length);
            Assert.All(tail, t => Assert.False(t.measured));
            var qids = System.Text.Encoding.UTF8.GetString(r.ServingLines!).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("op").GetString() == "S1"
                    && e.TryGetProperty("stratum", out var st) && st.GetString() == "Tail")
                .Select(e => e.GetProperty("qid").GetInt64()).ToArray();
            Assert.Equal(w.Concept.TailQids, qids);
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void CorrectnessExpectedAffectsCanonicalIdentity()
    {
        var a = WorkloadContractV1.Default().CanonicalNormative();
        var b = new WorkloadContractV1
        {
            CorrectnessOnly = new List<WorkloadContractV1.StratumDef>
            {
                new("S1", "Tail", WorkloadContractV1.SelectionModeAll, 21, null, null, 21),
            },
        }.CanonicalNormative();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Manifest_Deterministic_WithProvenance()
    {
        var w = SyntheticWorld.Build();
        string r1 = Path.Combine(Path.GetTempPath(), "mm1-" + Guid.NewGuid().ToString("N"));
        string r2 = Path.Combine(Path.GetTempPath(), "mm2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = BuildWorld(w);
            var provenance = new Dictionary<string, string> { ["t2"] = "t2sha", ["machine_contract"] = "mcsha", ["phase0_fixture"] = "fx" };
            foreach (var root in new[] { r1, r2 })
            {
                var pub = new WorkloadPublisher(root, "synth-1", "id".PadRight(64, 'x'));
                var pr = pub.Publish(WorkloadContractV1.Default(), r.ServingLines!, r.GraphLines!, r.ExpectedLines!, r.AnalyticalLines!,
                    r.PoolCardinalities, r.Continuity, r.MeasuredServingCount, r.MeasuredG1Count, r.G2BatchCount,
                    r.G1CandidatesConsidered, r.G1RejectedGuard, r.G1MaxVisited,
                    r.G2CandidatesConsidered, r.G2RejectedGuard, r.G2MaxVisited,
                    provenance);
                Assert.True(pr.Ok);
            }
            byte[] m1 = File.ReadAllBytes(Path.Combine(r1, "workload-v1", "manifest.json"));
            byte[] m2 = File.ReadAllBytes(Path.Combine(r2, "workload-v1", "manifest.json"));
            Assert.Equal(m1, m2);
            string text = System.Text.Encoding.UTF8.GetString(m1);
            Assert.Contains("\"t2\":\"t2sha\"", text);
            Assert.Contains("\"machine_contract\":\"mcsha\"", text);
            Assert.DoesNotContain("utc", text);
            Assert.DoesNotContain("/", text.Substring(0, Math.Min(200, text.Length)));
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
            try { Directory.Delete(r1, true); Directory.Delete(r2, true); } catch { /* ignore */ }
        }
    }
}
