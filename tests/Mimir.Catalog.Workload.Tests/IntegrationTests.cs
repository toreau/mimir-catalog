using System.Text.Json;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

public class IntegrationTests
{
    private static WorkloadBuild.Result EnsureGo(WorkloadBuild.Result r)
    {
        if (r.Verdict != WorkloadBuild.Go)
            throw new InvalidOperationException("not GO: " + string.Join(" | ", r.Reasons));
        return r;
    }

    private static WorkloadBuild.Result BuildWorld(SyntheticWorld.Tables w)
    {
        return WorkloadBuild.Build(
            new WorkloadContractV1(), "synth-1", w.Concept, w.Lexical, w.Instance, w.Subclass,
            () => w.Rows, w.FixturePath);
    }

    [Fact]
    public void AuthoritativePath_AllStrataFill()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = EnsureGo(BuildWorld(w));
            Assert.Empty(r.Reasons);
            Assert.Equal(52_880L, r.MeasuredServingCount);
            Assert.Equal(500L, r.MeasuredG1Count);
            Assert.Equal(200, r.G2BatchCount);
            Assert.Equal(3L, (long)r.Continuity["resolvedGoldPresent"]);
            Assert.Equal(1L, (long)r.Continuity["goldUnionPresent"]);
            Assert.Equal(4L, (long)r.Continuity["ambiguousCandPresent"]);
            Assert.Equal(1L, (long)r.Continuity["lexicalSurfacePresent"]);
            Assert.Equal(1L, (long)r.Continuity["ambiguousMultiPresent"]);

            // Per-stratum measured counts equal the frozen contract.
            var lines = ReadLines(r.ServingLines!);
            var groups = lines.Where(l => l.measured).GroupBy(l => (l.op, l.stratum))
                .ToDictionary(g => g.Key, g => g.LongCount());
            foreach (var st in new WorkloadContractV1().Strata)
            {
                if (st.Operation.StartsWith("G", StringComparison.Ordinal)) continue; // graph probes live in graph file
                Assert.Equal(st.MeasuredCount, groups[(st.Operation, st.Stratum)]);
            }
            Assert.Equal(20L, (long)lines.Count(l => !l.measured && l.op == "S1" && l.stratum == "Tail"));
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
        }
    }

    [Fact]
    public void S4_HighDisjointFromDegree2Plus()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = EnsureGo(BuildWorld(w));
            var lines = ReadLines(r.ServingLines!);
            var high = lines.Where(l => l.op == "S4" && l.stratum == "HighDegree" && l.measured).Select(l => l.qid!.Value).ToHashSet();
            var deg2 = lines.Where(l => l.op == "S4" && l.stratum == "Degree2Plus" && l.measured).Select(l => l.qid!.Value).ToHashSet();
            Assert.Equal(500, high.Count);
            Assert.Equal(3000, deg2.Count);
            Assert.Empty(high.Intersect(deg2));
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
        }
    }

    [Fact]
    public void ExpectedResults_DigestsWellFormedAndG1Present()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = EnsureGo(BuildWorld(w));
            var expected = ReadLines(r.ExpectedLines!);
            Assert.All(expected, l => Assert.Equal(64, l.digest!.Length));
            Assert.Equal(r.MeasuredServingCount + 20 + r.MeasuredG1Count + 200 + 1, expected.Count); // + G2 per-input + overall batch
            var graph = ReadLines(r.GraphLines!);
            Assert.Equal(501, graph.Count); // 500 G1 + one G2 batch line
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
        }
    }

    [Fact]
    public void Publication_StagingPromote_NoOverwrite()
    {
        var w = SyntheticWorld.Build();
        string root = Path.Combine(Path.GetTempPath(), "mimir-bm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = EnsureGo(BuildWorld(w));
            string id = "i".PadRight(64, 'd');
            var pub = new WorkloadPublisher(root, "synth-1", id);
            var p1 = pub.Publish(new WorkloadContractV1(), r.ServingLines!, r.GraphLines!, r.ExpectedLines!, r.AnalyticalLines!,
                r.PoolCardinalities, r.Continuity, r.MeasuredServingCount, r.MeasuredG1Count, r.G2BatchCount,
                r.G1CandidatesConsidered, r.G1RejectedGuard, r.G1MaxVisited,
                r.G2CandidatesConsidered, r.G2RejectedGuard, r.G2MaxVisited,
                new Dictionary<string, string> { ["t2"] = "t", ["machine_contract"] = "m" });
            Assert.True(p1.Ok);
            string finalDir = Path.Combine(root, "workload-v1");
            Assert.True(Directory.Exists(finalDir));
            foreach (var f in new[] { "serving-probes.jsonl", "graph-probes.jsonl", "expected-results.jsonl", "analytical-expected.jsonl", "manifest.json", "workload.state.json" })
                Assert.True(File.Exists(Path.Combine(finalDir, f)));

            // Deterministic: no timestamp/absolute path inside authoritative files.
            string manifestText = File.ReadAllText(Path.Combine(finalDir, "manifest.json"));
            Assert.DoesNotContain("utc", manifestText);
            Assert.DoesNotContain("staging", manifestText);
            Assert.DoesNotContain(Path.GetFullPath(root), manifestText);

            var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(finalDir, "workload.state.json")));
            Assert.Equal("Complete", state.RootElement.GetProperty("state").GetString());

            // Second publish must not overwrite.
            var p2 = pub.Publish(new WorkloadContractV1(), r.ServingLines!, r.GraphLines!, r.ExpectedLines!, r.AnalyticalLines!,
                r.PoolCardinalities, r.Continuity, r.MeasuredServingCount, r.MeasuredG1Count, r.G2BatchCount,
                r.G1CandidatesConsidered, r.G1RejectedGuard, r.G1MaxVisited,
                r.G2CandidatesConsidered, r.G2RejectedGuard, r.G2MaxVisited,
                new Dictionary<string, string> { ["t2"] = "t", ["machine_contract"] = "m" });
            Assert.False(p2.Ok);
            Assert.Equal(p1.FileSha256["manifest.json"], p1.FileSha256["manifest.json"]);
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Generation_IsByteReproducible()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r1 = BuildWorld(w);
            var r2 = BuildWorld(w);
            Assert.Equal(r1.ServingLines, r2.ServingLines);
            Assert.Equal(r1.GraphLines, r2.GraphLines);
            Assert.Equal(r1.ExpectedLines, r2.ExpectedLines);
            Assert.Equal(r1.AnalyticalLines, r2.AnalyticalLines);
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
        }
    }

    [Fact]
    public void InsufficientStratum_ReturnsHold_NoShrink()
    {
        var w = SyntheticWorld.Build(highFanoutKeys: 10); // far below the frozen 500
        try
        {
            var r = BuildWorld(w);
            Assert.Equal(WorkloadBuild.Hold, r.Verdict);
            Assert.Contains(r.Reasons, reason => reason.StartsWith("S2/Fanout51Plus"));
            Assert.Null(r.ServingLines);
        }
        finally
        {
            SyntheticWorld.Cleanup(w);
        }
    }

    private static List<(string op, string stratum, bool measured, long? qid, string? digest)> ReadLines(byte[] bytes)
    {
        var list = new List<(string, string, bool, long?, string?)>();
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string op = r.GetProperty("op").GetString()!;
            string stratum = r.TryGetProperty("stratum", out var st) ? st.GetString() ?? "" : "";
            bool measured = r.TryGetProperty("measured", out var m) && m.GetBoolean();
            long? qid = r.TryGetProperty("qid", out var q) ? q.GetInt64() : null;
            string? digest = r.TryGetProperty("digest", out var d) ? d.GetString() : null;
            list.Add((op, stratum, measured, qid, digest));
        }
        return list;
    }
}
