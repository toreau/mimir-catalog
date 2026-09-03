using System.Text.Json;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Package-level self-consistency validation for the generated workload
/// artifacts. Runs on the in-memory package before publication; any failure is
/// a HOLD and prevents an internally invalid (but byte-reproducible) package
/// from being called GO.
/// </summary>
public static class WorkloadPackageValidator
{
    public sealed class Outcome
    {
        public bool Ok { get; set; } = true;
        public List<string> Reasons { get; } = new();
    }

    private static JsonElement[] ReadLines(byte[] bytes)
    {
        var list = new List<JsonElement>();
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            list.Add(JsonDocument.Parse(line).RootElement.Clone());
        return list.ToArray();
    }

    public static Outcome Validate(
        WorkloadContractV1 c,
        byte[] servingBytes,
        byte[] graphBytes,
        byte[] expectedBytes,
        byte[] analyticalBytes)
    {
        var o = new Outcome();
        var serving = ReadLines(servingBytes);
        var graph = ReadLines(graphBytes);
        var expected = ReadLines(expectedBytes);
        var analytical = ReadLines(analyticalBytes);

        void Fail(string m) { o.Ok = false; o.Reasons.Add(m); }

        string Op(JsonElement e) => e.GetProperty("op").GetString()!;
        long Seq(JsonElement e) => e.GetProperty("seq").GetInt64();
        string Stratum(JsonElement e) => e.TryGetProperty("stratum", out var s) ? s.GetString() ?? "" : "";

        // ---- serving: measured count matches contract, expected keys == probe keys ----
        var servingMeasured = serving.Count(e => e.GetProperty("measured").GetBoolean());
        long expectedServingMeasured = c.Strata.Where(s => s.Operation != "G1").Sum(s => s.MeasuredCount);
        if (servingMeasured != expectedServingMeasured) Fail($"serving measured {servingMeasured} != contract {expectedServingMeasured}");

        var probeSeen = new HashSet<(string, long)>();
        foreach (var e in serving)
            if (!probeSeen.Add((Op(e), Seq(e)))) Fail($"duplicate serving probe key ({Op(e)},{Seq(e)})");
        var expectedServingSeen = new HashSet<(string, long)>();
        foreach (var e in expected.Where(e => Op(e) != "G1" && Op(e) != "G2"))
            if (!expectedServingSeen.Add((Op(e), Seq(e)))) Fail($"duplicate expected serving key ({Op(e)},{Seq(e)})");

        var probeKeys = serving.Select(e => (Op(e), Seq(e))).ToHashSet();
        var expectedServingKeys = expected.Where(e => Op(e) != "G1" && Op(e) != "G2").Select(e => (Op(e), Seq(e))).ToHashSet();
        if (!probeKeys.SetEquals(expectedServingKeys)) Fail("serving expected keys do not match serving probe keys");

        var tailProbes = serving.Where(e => Op(e) == "S1" && Stratum(e) == "Tail").ToArray();
        var tailExpected = c.CorrectnessOnly.Single();
        if (tailProbes.Length != tailExpected.MeasuredCount) Fail($"tail probes {tailProbes.Length} != contract {tailExpected.MeasuredCount}");
        if (tailProbes.Any(e => e.GetProperty("measured").GetBoolean())) Fail("tail probes must be measured=false");

        // ---- G1: graph/expected one-to-one ----
        var g1Probes = graph.Where(e => Op(e) == "G1").Select(e => Seq(e)).ToArray();
        var g1Expected = expected.Where(e => Op(e) == "G1").Select(e => Seq(e)).ToArray();
        long expectedG1 = c.Strata.Where(s => s.Operation == "G1").Sum(s => s.MeasuredCount);
        if (g1Probes.Length != expectedG1) Fail($"G1 graph probes {g1Probes.Length} != contract {expectedG1}");
        if (g1Probes.Distinct().Count() != g1Probes.Length) Fail("G1 graph seqs not unique");
        if (g1Expected.Length != expectedG1) Fail($"G1 expected rows {g1Expected.Length} != contract {expectedG1}");
        if (g1Expected.Distinct().Count() != g1Expected.Length) Fail("G1 expected seqs not unique");
        var g1ProbeSet = g1Probes.ToHashSet();
        var g1ExpectedSet = g1Expected.ToHashSet();
        if (!g1ProbeSet.SetEquals(g1ExpectedSet)) Fail("G1 graph/expected seq key sets differ");
        for (int i = 0; i < expectedG1; i++)
            if (!g1ProbeSet.Contains(i)) Fail($"G1 missing seq {i}");

        // ---- G2: batch probe materialized with positional per-input mapping ----
        var g2Batches = graph.Where(e => Op(e) == "G2" && e.TryGetProperty("measured", out var mm) && mm.GetBoolean()).ToArray();
        if (g2Batches.Length != 1) { Fail("expected exactly one G2 measured batch probe"); return o; }
        var batch = g2Batches[0];
        long batchSeq = Seq(batch);
        var batchItems = batch.GetProperty("concepts").EnumerateArray().Select(e => (qid: e.GetProperty("qid").GetInt64(), src: e.GetProperty("source_stratum").GetString()!)).ToArray();
        if (batchItems.Length != c.G2BatchConcepts) Fail($"G2 serialized concepts {batchItems.Length} != {c.G2BatchConcepts}");
        if (batchItems.Select(b => b.qid).Distinct().Count() != batchItems.Length) Fail("G2 serialized QIDs not unique");
        if (batchItems.Count(b => b.src == "P31Degree1") != 100) Fail("G2 source P31Degree1 count != 100");
        if (batchItems.Count(b => b.src == "P31Degree2Plus") != 100) Fail("G2 source P31Degree2Plus count != 100");
        if (batchSeq != g1Expected.Length) Fail($"G2 batch seq {batchSeq} != G1 count {g1Expected.Length}");

        var g2ExpectedRows = expected.Where(e => Op(e) == "G2").ToArray();
        if (g2ExpectedRows.Any(e => Seq(e) != batchSeq)) Fail("G2 expected rows must share the G2 batch seq");
        var g2PerInput = g2ExpectedRows.Where(e => e.TryGetProperty("kind", out var k) && k.GetString() == "PerInput").ToArray();
        var g2BatchExpected = g2ExpectedRows.Where(e => e.TryGetProperty("kind", out var k) && k.GetString() == "Batch").ToArray();
        if (g2BatchExpected.Length != 1) Fail("expected exactly one G2 overall batch expected result");
        if (g2PerInput.Length != c.G2BatchConcepts) Fail($"G2 per-input expected {g2PerInput.Length} != {c.G2BatchConcepts}");
        if (g2PerInput.Any(e => e.GetProperty("measured").GetBoolean())) Fail("G2 per-input expected must be measured=false");
        if (g2PerInput.Select(e => e.GetProperty("item").GetInt64()).Distinct().Count() != g2PerInput.Length)
            Fail("G2 per-input item indices not unique");
        // Positional mapping: PerInput.item i must equal batch.concepts[i].
        var byItem = g2PerInput.ToDictionary(e => e.GetProperty("item").GetInt64(), e => e);
        for (int i = 0; i < batchItems.Length; i++)
        {
            if (!byItem.TryGetValue(i, out var row)) { Fail($"missing G2 per-input item {i}"); continue; }
            if (row.GetProperty("qid").GetInt64() != batchItems[i].qid) Fail($"G2 per-input item {i} qid mismatch");
            if (row.GetProperty("source_stratum").GetString() != batchItems[i].src) Fail($"G2 per-input item {i} source_stratum mismatch");
        }

        // ---- analytical: exactly the eight frozen operations ----
        var analyticalOps = analytical.Select(Op).ToHashSet();
        var expectedOps = WorkloadContractV1.AnalyticalOperations.ToHashSet();
        if (!analyticalOps.SetEquals(expectedOps) || analytical.Length != 8) Fail("analytical expected must be exactly the eight A1-A5 operations");

        return o;
    }
}
