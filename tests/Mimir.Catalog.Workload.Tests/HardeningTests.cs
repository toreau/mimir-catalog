using System.Text.Json;
using System.Text.Json.Nodes;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

/// <summary>
/// Final hardening tests: required stratum presence, per-stratum selectionMode
/// compatibility, G2 accepted-only guard instrumentation, and strengthened
/// package validation (duplicate serving keys, G2 positional/seq integrity).
/// </summary>
public class HardeningTests
{
    private static JsonObject Tracked() =>
        JsonNode.Parse(File.ReadAllText(RepoRel("benchmarks/workload-contract-v1.json")))!.AsObject();

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

    private static void Reject(Func<JsonObject, JsonObject> edit) =>
        Assert.Throws<InvalidDataException>(() => WorkloadContractV1.Parse(System.Text.Encoding.UTF8.GetBytes(edit(Tracked()).ToJsonString())));

    private static JsonObject Stratum(JsonObject root, string op, string stratum)
    {
        foreach (var node in root["strata"]!.AsArray())
        {
            var s = node!.AsObject();
            if (s["operation"]?.GetValue<string>() == op && s["stratum"]?.GetValue<string>() == stratum) return s;
        }
        throw new InvalidDataException($"stratum not found {op}/{stratum}");
    }

    private static JsonObject[] Nodes(byte[] bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => (JsonObject)JsonNode.Parse(l)!).ToArray();

    private static byte[] Join(IEnumerable<JsonObject> nodes) =>
        System.Text.Encoding.UTF8.GetBytes(string.Join('\n', nodes.Select(n => n.ToJsonString())) + "\n");

    // ---- required stratum presence ----
    [Theory]
    [InlineData("S1", "T1Only")]
    [InlineData("S2", "Fanout51Plus")]
    [InlineData("S5", "Degree2Plus")]
    [InlineData("G1", "Degree1")]
    public void Contract_Rejects_MissingRequiredStratum(string op, string stratum)
    {
        Reject(root =>
        {
            var arr = root["strata"]!.AsArray();
            int i = 0;
            foreach (var node in arr.ToList())
            {
                var s = node!.AsObject();
                if (s["operation"]?.GetValue<string>() == op && s["stratum"]?.GetValue<string>() == stratum)
                {
                    arr.RemoveAt(i);
                    break;
                }
                i++;
            }
            return root;
        });
    }

    // ---- per-stratum selectionMode compatibility ----
    [Theory]
    [InlineData("S1", "Absent", "sha256-ranked-sample")]
    [InlineData("S1", "T1Only", "all-eligible")]
    [InlineData("S2", "Fanout51Plus", "sha256-ranked-sample")]
    [InlineData("S2", "Miss", "sha256-ranked-sample")]
    [InlineData("S4", "HighDegree", "sha256-ranked-sample")]
    [InlineData("G1", "Degree1", "all-eligible")]
    public void Contract_Rejects_IncompatibleSelectionMode(string op, string stratum, string mode)
    {
        Reject(root =>
        {
            Stratum(root, op, stratum)["selectionMode"] = JsonValue.Create(mode);
            return root;
        });
    }

    [Fact]
    public void Contract_Count_ExactMandatoryStrata()
    {
        var c = WorkloadContractV1.Parse(File.ReadAllBytes(RepoRel("benchmarks/workload-contract-v1.json")));
        Assert.Equal(21, c.Strata.Count);
    }

    // ---- G2 accepted-only max visited ----
    [Fact]
    public void G2MaxVisited_ReflectsOnlyAcceptedCandidates()
    {
        // Deep chain root: 1 -> 2 -> 3 -> 4 (visited 4 with guard 3 -> rejected).
        var parents = new Dictionary<long, long[]>
        {
            [1001] = new[] { 1002L },
            [1002] = new[] { 1003L },
            [1003] = new[] { 1004L },
        };

        // Pick a chain QID whose rank is mid-list so it is certainly considered
        // before 100 leaves are accepted.
        var leaves = Enumerable.Range(2000, 100).Select(i => (long)i).ToArray();
        long chainQid = 0;
        int chosenIdx = -1;
        for (long cand = 5000; cand < 50_000 && chosenIdx < 0; cand += 1)
        {
            var pool = leaves.Append(cand).ToArray();
            var ordered = WorkloadSelection.RankTopQids(pool, "d", "G2", "P31Degree2Plus", pool.Length);
            for (int i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].Qid == cand && i is > 0 and < 99)
                {
                    chainQid = cand;
                    chosenIdx = i;
                    break;
                }
            }
        }
        Assert.True(chainQid != 0, "could not find a chain qid with suitable rank");

        long[]? targets(long q) => q == chainQid ? new[] { 1001L } : (q >= 2000 ? new[] { q + 100_000L } : null);

        var spec = new[] { ("P31Degree2Plus", 100L, (IReadOnlyList<long>)leaves.Append(chainQid).ToArray()) };
        var outcome = G2BatchSelection.Select(
            spec,
            targets,
            q => parents.TryGetValue(q, out var p) ? p : Array.Empty<long>(),
            "d",
            maxDepth: 3,
            guard: 3);

        Assert.Equal(1, outcome.Rejected);
        Assert.Equal(100, outcome.Inputs.Count);
        Assert.Equal(1, outcome.MaxVisitedAccepted); // rejected candidate (visited up to 3) does not contribute
        Assert.Equal(100, outcome.Inputs.Count(i => i.Source == "P31Degree2Plus"));
        Assert.True(outcome.Considered > 100);
    }

    // ---- package validator hardening ----
    private static WorkloadBuild.Result BuildWorld(SyntheticWorld.Tables w)
        => WorkloadBuild.Build(WorkloadContractV1.Default(), "synth-1", w.Concept, w.Lexical, w.Instance, w.Subclass, () => w.Rows, w.FixturePath);

    [Fact]
    public void Package_Rejects_DuplicateServingKey()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var nodes = Nodes(r.ServingLines!);
            var measured = nodes.Where(n => n["measured"]?.GetValue<bool>() == true).Take(2).ToArray();
            measured[1]["seq"] = measured[0]["seq"].DeepClone();
            byte[] tampered = Join(nodes);
            var o = WorkloadPackageValidator.Validate(WorkloadContractV1.Default(), tampered, r.GraphLines!, r.ExpectedLines!, r.AnalyticalLines!);
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, x => x.Contains("duplicate serving probe key"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void Package_Rejects_G2SeqMismatch()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var nodes = Nodes(r.GraphLines!);
            var batch = nodes.Single(n => n["op"]?.GetValue<string>() == "G2");
            batch["seq"] = 501;
            byte[] tampered = Join(nodes);
            var o = WorkloadPackageValidator.Validate(WorkloadContractV1.Default(), r.ServingLines!, tampered, r.ExpectedLines!, r.AnalyticalLines!);
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, x => x.Contains("G2 batch seq"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void Package_Rejects_G2PositionalQidSwap()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var nodes = Nodes(r.ExpectedLines!).ToList();
            var per = nodes.Where(n => n["op"]?.GetValue<string>() == "G2" && n["kind"]?.GetValue<string>() == "PerInput").ToList();
            Assert.Equal(200, per.Count);
            long q0 = per[0]["qid"]!.GetValue<long>();
            long q1 = per[1]["qid"]!.GetValue<long>();
            per[0]["qid"] = q1;
            per[1]["qid"] = q0;
            byte[] tampered = Join(nodes);
            var o = WorkloadPackageValidator.Validate(WorkloadContractV1.Default(), r.ServingLines!, r.GraphLines!, tampered, r.AnalyticalLines!);
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, x => x.Contains("qid mismatch"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }

    [Fact]
    public void Package_Rejects_G2PerInputSeqDivergence()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = BuildWorld(w);
            var nodes = Nodes(r.ExpectedLines!).ToList();
            var per = nodes.First(n => n["op"]?.GetValue<string>() == "G2" && n["kind"]?.GetValue<string>() == "PerInput");
            per["seq"] = 12345;
            byte[] tampered = Join(nodes);
            var o = WorkloadPackageValidator.Validate(WorkloadContractV1.Default(), r.ServingLines!, r.GraphLines!, tampered, r.AnalyticalLines!);
            Assert.False(o.Ok);
            Assert.Contains(o.Reasons, x => x.Contains("share the G2 batch seq"));
        }
        finally { SyntheticWorld.Cleanup(w); }
    }
}
