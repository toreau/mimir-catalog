using System.Security.Cryptography;
using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Serving-specific authoritative loader for the published workload package.
/// Validates Complete publication state, authoritative manifest/artifact
/// identities and internal serving key invariants. Does not require graph or
/// analytical artifacts.
/// </summary>
public static class ServingWorkloadLoader
{
    public const string OfficialWorkloadId = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc";
    public const string OfficialCorpusId = "511adb9ebd066f1d4d344b80171902d5";
    public const string ExpectedManifestSha = "02ca19be526ad76d42b4681d6680d899aa51f99f8eed755333dfdec366f5776e";
    public const string ExpectedServingSha = "bc0b0a4d76c2a4f7a2caac6bc3a7f9aea75f111e5ec1355c42f4ca246251fd6f";
    public const string ExpectedResultsSha = "ac45df6e625ce863093612dca8f6a6d8b3eca64b64f3c672e98769fbc48226b8";

    internal sealed record Identity(string ManifestSha, string WorkloadId, string CorpusId, string ServingSha, string ResultsSha);

    private static readonly Identity Authoritative = new(ExpectedManifestSha, OfficialWorkloadId, OfficialCorpusId, ExpectedServingSha, ExpectedResultsSha);

    public static ServingWorkload Load(string workloadDir) => LoadCore(workloadDir, Authoritative);

    /// <summary>Internal fixture seam for tests; still enforces complete state and structural key invariants.</summary>
    internal static ServingWorkload LoadFixture(string workloadDir, Identity identity) => LoadCore(workloadDir, identity);

    private static ServingWorkload LoadCore(string workloadDir, Identity id)
    {
        string statePath = Path.Combine(workloadDir, "workload.state.json");
        string manifestPath = Path.Combine(workloadDir, "manifest.json");
        string servingPath = Path.Combine(workloadDir, "serving-probes.jsonl");
        string resultsPath = Path.Combine(workloadDir, "expected-results.jsonl");
        foreach (var p in new[] { statePath, manifestPath, servingPath, resultsPath })
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

        string servingSha = Sha256(servingPath);
        string resultsSha = Sha256(resultsPath);
        string manifestServing = ManifestFileSha(m, "serving-probes.jsonl");
        string manifestResults = ManifestFileSha(m, "expected-results.jsonl");
        if (servingSha != manifestServing || servingSha != id.ServingSha)
            throw new InvalidDataException("serving-probes identity mismatch");
        if (resultsSha != manifestResults || resultsSha != id.ResultsSha)
            throw new InvalidDataException("expected-results identity mismatch");

        // Probes.
        var probes = new List<ServingProbe>();
        var probeKeys = new HashSet<(string, long)>();
        foreach (var line in File.ReadLines(servingPath))
        {
            using var e = JsonDocument.Parse(line);
            var r = e.RootElement;
            string op = r.GetProperty("op").GetString()!;
            if (!IsServingOp(op)) continue;
            long seq = r.GetProperty("seq").GetInt64();
            if (!probeKeys.Add((op, seq))) throw new InvalidDataException($"duplicate serving probe key ({op},{seq})");
            bool measured = r.TryGetProperty("measured", out var mm) && mm.GetBoolean();
            string stratum = r.TryGetProperty("stratum", out var st2) ? st2.GetString() ?? "" : "";
            long? qid = r.TryGetProperty("qid", out var q) ? q.GetInt64() : null;
            string? lang = r.TryGetProperty("lang", out var l) ? l.GetString() : null;
            string? value = r.TryGetProperty("value", out var v) ? v.GetString() : null;
            probes.Add(new ServingProbe(op, seq, stratum, measured, qid, lang, value));
        }
        if (probes.Count == 0) throw new InvalidDataException("no serving probes loaded");

        // Expected (ignore graph/analytical rows).
        var expected = new Dictionary<(string, long), ServingExpected>();
        var expectedKeys = new HashSet<(string, long)>();
        foreach (var line in File.ReadLines(resultsPath))
        {
            using var e = JsonDocument.Parse(line);
            var r = e.RootElement;
            string op = r.GetProperty("op").GetString()!;
            if (!IsServingOp(op)) continue;
            long seq = r.GetProperty("seq").GetInt64();
            if (!expectedKeys.Add((op, seq))) throw new InvalidDataException($"duplicate expected serving key ({op},{seq})");
            bool measured = r.TryGetProperty("measured", out var mm) && mm.GetBoolean();
            long card = r.TryGetProperty("cardinality", out var ca) ? ca.GetInt64() : 0;
            string digest = r.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            expected[(op, seq)] = new ServingExpected(op, seq, measured, card, digest);
        }

        if (!probeKeys.SetEquals(expectedKeys))
            throw new InvalidDataException("serving probe key set != expected-result key set");

        foreach (var p in probes)
            if (p.Measured != expected[(p.Op, p.Seq)].Measured)
                throw new InvalidDataException($"measured flag mismatch on ({p.Op},{p.Seq})");

        return new ServingWorkload { Probes = probes, Expected = expected };
    }

    private static string ManifestFileSha(JsonElement manifest, string file)
    {
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty(file, out var f))
            throw new InvalidDataException($"manifest missing {file} identity");
        return f.GetString() ?? "";
    }

    private static bool IsServingOp(string op) => op is "S1" or "S2" or "S3" or "S4" or "S5";

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }
}
