using System.Security.Cryptography;
using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Authoritative G1 graph workload loader. Validates Complete publication
/// state, manifest/artifact identities, and G1 structural invariants. G2
/// probes and expected rows are excluded from the G1 model.
/// </summary>
public static class GraphWorkloadLoader
{
    public const string OfficialWorkloadId = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc";
    public const string OfficialCorpusId = "511adb9ebd066f1d4d344b80171902d5";
    public const string ExpectedManifestSha = "02ca19be526ad76d42b4681d6680d899aa51f99f8eed755333dfdec366f5776e";
    public const string ExpectedGraphSha = "faf1907a42d5ef57489948bd0b61b763efcf8d1a81ea23645868229ff216100b";
    public const string ExpectedResultsSha = "ac45df6e625ce863093612dca8f6a6d8b3eca64b64f3c672e98769fbc48226b8";

    internal sealed record Identity(string ManifestSha, string WorkloadId, string CorpusId, string GraphSha, string ResultsSha);

    private static readonly Identity Authoritative = new(ExpectedManifestSha, OfficialWorkloadId, OfficialCorpusId, ExpectedGraphSha, ExpectedResultsSha);

    public static GraphWorkload Load(string workloadDir) => LoadCore(workloadDir, Authoritative);

    /// <summary>Internal fixture seam for tests; structural invariants are still enforced.</summary>
    internal static GraphWorkload LoadFixture(string workloadDir, Identity identity) => LoadCore(workloadDir, identity);

    private static GraphWorkload LoadCore(string workloadDir, Identity id)
    {
        string statePath = Path.Combine(workloadDir, "workload.state.json");
        string manifestPath = Path.Combine(workloadDir, "manifest.json");
        string graphPath = Path.Combine(workloadDir, "graph-probes.jsonl");
        string resultsPath = Path.Combine(workloadDir, "expected-results.jsonl");
        foreach (var p in new[] { statePath, manifestPath, graphPath, resultsPath })
            if (!File.Exists(p)) throw new InvalidDataException($"workload package missing {Path.GetFileName(p)}");

        using (var st = JsonDocument.Parse(File.ReadAllBytes(statePath)))
            if (!st.RootElement.TryGetProperty("state", out var s) || s.GetString() != "Complete")
                throw new InvalidDataException("workload publication state != Complete");

        if (Sha256(manifestPath) != id.ManifestSha) throw new InvalidDataException("authoritative manifest identity mismatch");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var m = doc.RootElement;
        string wid = m.TryGetProperty("workload_id", out var w) ? w.GetString() ?? "" : "";
        string corpus = m.TryGetProperty("corpus_id", out var c) ? c.GetString() ?? "" : "";
        if (wid != id.WorkloadId || corpus != id.CorpusId) throw new InvalidDataException("workload manifest identity mismatch");

        string graphSha = Sha256(graphPath);
        string resultsSha = Sha256(resultsPath);
        if (graphSha != ManifestFileSha(m, "graph-probes.jsonl") || graphSha != id.GraphSha)
            throw new InvalidDataException("graph-probes identity mismatch");
        if (resultsSha != ManifestFileSha(m, "expected-results.jsonl") || resultsSha != id.ResultsSha)
            throw new InvalidDataException("expected-results identity mismatch");

        // G1 probes only (G2 batch probe is excluded).
        var probes = new List<GraphProbe>();
        var probeKeys = new HashSet<(string, long)>();
        foreach (var line in File.ReadLines(graphPath))
        {
            using var e = JsonDocument.Parse(line);
            var r = e.RootElement;
            if (r.GetProperty("op").GetString() != "G1") continue;
            long seq = r.GetProperty("seq").GetInt64();
            if (!probeKeys.Add(("G1", seq))) throw new InvalidDataException($"duplicate G1 probe key (G1,{seq})");
            bool measured = r.TryGetProperty("measured", out var mm) && mm.GetBoolean();
            if (!measured) throw new InvalidDataException($"G1 probe (G1,{seq}) must be measured=true");
            string stratum = r.TryGetProperty("stratum", out var st2) ? st2.GetString() ?? "" : "";
            if (!r.TryGetProperty("start", out var start)) throw new InvalidDataException($"G1 probe (G1,{seq}) missing start");
            probes.Add(new GraphProbe("G1", seq, stratum, true, start.GetInt64()));
        }
        if (probes.Count != 500) throw new InvalidDataException($"G1 probe count {probes.Count} != 500");
        if (!probes.Select(p => p.Seq).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, 500).Select(i => (long)i)))
            throw new InvalidDataException("G1 seq set must be exactly 0..499");
        if (probes.Count(p => p.Stratum == "Degree1") != 250 || probes.Count(p => p.Stratum == "Degree2Plus") != 250)
            throw new InvalidDataException("G1 strata must be Degree1 250 / Degree2Plus 250");

        // G1 expected rows only (exclude G2 PerInput/Batch and serving rows).
        var expected = new Dictionary<(string, long), GraphExpected>();
        var expectedKeys = new HashSet<(string, long)>();
        foreach (var line in File.ReadLines(resultsPath))
        {
            using var e = JsonDocument.Parse(line);
            var r = e.RootElement;
            if (r.GetProperty("op").GetString() != "G1") continue;
            long seq = r.GetProperty("seq").GetInt64();
            if (!expectedKeys.Add(("G1", seq))) throw new InvalidDataException($"duplicate G1 expected key (G1,{seq})");
            if (!r.TryGetProperty("cardinality", out var card) || !r.TryGetProperty("visited", out var visited) || !r.TryGetProperty("digest", out var digest))
                throw new InvalidDataException($"G1 expected (G1,{seq}) missing cardinality/visited/digest");
            bool measured = r.TryGetProperty("measured", out var mm) && mm.GetBoolean();
            if (!measured) throw new InvalidDataException($"G1 expected (G1,{seq}) must be measured=true");
            expected[("G1", seq)] = new GraphExpected("G1", seq, true, card.GetInt64(), visited.GetInt64(), digest.GetString() ?? "");
        }
        if (expected.Count != 500) throw new InvalidDataException($"G1 expected count {expected.Count} != 500");

        if (!probeKeys.SetEquals(expectedKeys)) throw new InvalidDataException("G1 probe key set != G1 expected key set");

        return new GraphWorkload { Probes = probes, Expected = expected };
    }

    private static string ManifestFileSha(JsonElement manifest, string file)
    {
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty(file, out var f))
            throw new InvalidDataException($"manifest missing {file} identity");
        return f.GetString() ?? "";
    }

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }

    public static G2Workload LoadG2(string workloadDir) => LoadG2Core(workloadDir, Authoritative);

    /// <summary>Internal fixture seam for G2 tests.</summary>
    internal static G2Workload LoadG2Fixture(string workloadDir, Identity identity) => LoadG2Core(workloadDir, identity);

    private static G2Workload LoadG2Core(string workloadDir, Identity id)
    {
        string statePath = Path.Combine(workloadDir, "workload.state.json");
        string manifestPath = Path.Combine(workloadDir, "manifest.json");
        string graphPath = Path.Combine(workloadDir, "graph-probes.jsonl");
        string resultsPath = Path.Combine(workloadDir, "expected-results.jsonl");
        foreach (var p in new[] { statePath, manifestPath, graphPath, resultsPath })
            if (!File.Exists(p)) throw new InvalidDataException($"workload package missing {Path.GetFileName(p)}");

        using (var st = JsonDocument.Parse(File.ReadAllBytes(statePath)))
            if (!st.RootElement.TryGetProperty("state", out var s) || s.GetString() != "Complete")
                throw new InvalidDataException("workload publication state != Complete");
        if (Sha256(manifestPath) != id.ManifestSha) throw new InvalidDataException("authoritative manifest identity mismatch");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var m = doc.RootElement;
        if ((m.TryGetProperty("workload_id", out var w) ? w.GetString() ?? "" : "") != id.WorkloadId
            || (m.TryGetProperty("corpus_id", out var c) ? c.GetString() ?? "" : "") != id.CorpusId)
            throw new InvalidDataException("workload manifest identity mismatch");

        string graphSha = Sha256(graphPath);
        string resultsSha = Sha256(resultsPath);
        if (graphSha != ManifestFileSha(m, "graph-probes.jsonl") || graphSha != id.GraphSha)
            throw new InvalidDataException("graph-probes identity mismatch");
        if (resultsSha != ManifestFileSha(m, "expected-results.jsonl") || resultsSha != id.ResultsSha)
            throw new InvalidDataException("expected-results identity mismatch");

        // Exactly one G2 batch probe.
        G2Concept[]? concepts = null;
        foreach (var line in File.ReadLines(graphPath))
        {
            using var e = JsonDocument.Parse(line);
            var r = e.RootElement;
            if (r.GetProperty("op").GetString() != "G2") continue;
            if (concepts != null) throw new InvalidDataException("multiple G2 batch probes");
            if (r.GetProperty("stratum").GetString() != "Batch") throw new InvalidDataException("G2 probe must be Batch");
            if (!r.TryGetProperty("measured", out var mm) || !mm.GetBoolean()) throw new InvalidDataException("G2 batch probe must be measured=true");
            if (r.TryGetProperty("seq", out var seq) && seq.GetInt64() != 500) throw new InvalidDataException("G2 batch probe seq must be 500");
            var arr = new List<G2Concept>();
            foreach (var cc in r.GetProperty("concepts").EnumerateArray())
                arr.Add(new G2Concept(cc.GetProperty("qid").GetInt64(), cc.GetProperty("source_stratum").GetString() ?? ""));
            concepts = arr.ToArray();
        }
        if (concepts == null) throw new InvalidDataException("G2 batch probe missing");
        if (concepts.Length != 200) throw new InvalidDataException($"G2 concept count {concepts.Length} != 200");
        if (concepts.Select(c => c.Qid).Distinct().Count() != 200) throw new InvalidDataException("G2 concepts not unique");
        if (concepts.Count(c => c.SourceStratum == "P31Degree1") != 100
            || concepts.Count(c => c.SourceStratum == "P31Degree2Plus") != 100)
            throw new InvalidDataException("G2 stratum composition must be 100 P31Degree1 / 100 P31Degree2Plus");

        // Expected rows.
        var perInput = new List<G2PerInputExpected>();
        G2BatchExpected? batch = null;
        var seen = new HashSet<int>();
        foreach (var line in File.ReadLines(resultsPath))
        {
            using var e = JsonDocument.Parse(line);
            var r = e.RootElement;
            if (r.GetProperty("op").GetString() != "G2") continue;
            string kind = r.GetProperty("kind").GetString() ?? "";
            if (!r.TryGetProperty("seq", out var seq) || seq.GetInt64() != 500) throw new InvalidDataException("G2 expected seq must be 500");
            if (kind == "PerInput")
            {
                int item = r.GetProperty("item").GetInt32();
                if (!seen.Add(item)) throw new InvalidDataException($"duplicate G2 PerInput item {item}");
                if (r.TryGetProperty("measured", out var mm) && mm.GetBoolean()) throw new InvalidDataException("G2 PerInput must be measured=false");
                perInput.Add(new G2PerInputExpected(item,
                    r.GetProperty("qid").GetInt64(),
                    r.GetProperty("source_stratum").GetString() ?? "",
                    r.GetProperty("cardinality").GetInt64(),
                    r.GetProperty("digest").GetString() ?? ""));
            }
            else if (kind == "Batch")
            {
                if (batch != null) throw new InvalidDataException("duplicate G2 Batch expected");
                if (!r.TryGetProperty("measured", out var mm) || !mm.GetBoolean()) throw new InvalidDataException("G2 Batch must be measured=true");
                batch = new G2BatchExpected(r.GetProperty("cardinality").GetInt64(), r.GetProperty("digest").GetString() ?? "");
            }
        }
        if (batch == null) throw new InvalidDataException("G2 Batch expected missing");
        if (perInput.Count != 200) throw new InvalidDataException($"G2 PerInput expected count {perInput.Count} != 200");
        var ordered = perInput.OrderBy(p => p.Item).ToArray();
        for (int i = 0; i < 200; i++)
        {
            if (ordered[i].Item != i) throw new InvalidDataException("G2 PerInput items must be exactly 0..199");
            if (ordered[i].Qid != concepts[i].Qid) throw new InvalidDataException($"positional QID mismatch at item {i}");
            if (ordered[i].SourceStratum != concepts[i].SourceStratum) throw new InvalidDataException($"positional source mismatch at item {i}");
        }
        return new G2Workload { Concepts = concepts, PerInput = ordered, Batch = batch };
    }
}
