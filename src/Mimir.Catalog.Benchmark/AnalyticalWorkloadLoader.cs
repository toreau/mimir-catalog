using System.Security.Cryptography;
using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Authoritative loader for the four A1 analytical expectations. Validates
/// Complete publication state and authoritative manifest/analytical identities;
/// does not require serving or graph artifacts.
/// </summary>
public static class AnalyticalWorkloadLoader
{
    public const string OfficialWorkloadId = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc";
    public const string OfficialCorpusId = "511adb9ebd066f1d4d344b80171902d5";
    public const string ExpectedManifestSha = "02ca19be526ad76d42b4681d6680d899aa51f99f8eed755333dfdec366f5776e";
    public const string ExpectedAnalyticalSha = "d5f2fc916c7ffe4b1e68821bde6b914df6cc013500b22b2bc04f2f0ca402bbef";

    private static readonly string[] LoadedOps = ["A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf", "A2", "A3", "A4", "A5"];

    internal sealed record Identity(string ManifestSha, string WorkloadId, string CorpusId, string AnalyticalSha);

    private static readonly Identity Authoritative = new(ExpectedManifestSha, OfficialWorkloadId, OfficialCorpusId, ExpectedAnalyticalSha);

    public static AnalyticalWorkload Load(string workloadDir) => LoadCore(workloadDir, Authoritative);

    /// <summary>Internal fixture seam; structural gates are still enforced.</summary>
    internal static AnalyticalWorkload LoadFixture(string workloadDir, Identity identity) => LoadCore(workloadDir, identity);

    private static AnalyticalWorkload LoadCore(string workloadDir, Identity id)
    {
        string statePath = Path.Combine(workloadDir, "workload.state.json");
        string manifestPath = Path.Combine(workloadDir, "manifest.json");
        string analyticalPath = Path.Combine(workloadDir, "analytical-expected.jsonl");
        foreach (var p in new[] { statePath, manifestPath, analyticalPath })
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

        string analyticalSha = Sha256(analyticalPath);
        if (analyticalSha != (m.TryGetProperty("files", out var files) && files.TryGetProperty("analytical-expected.jsonl", out var f)
            ? f.GetString() ?? "" : "") || analyticalSha != id.AnalyticalSha)
            throw new InvalidDataException("analytical-expected identity mismatch");

        var expected = new Dictionary<string, A1Expected>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(analyticalPath))
        {
            using var e = JsonDocument.Parse(line);
            string op = e.RootElement.GetProperty("op").GetString()!;
            if (!LoadedOps.Contains(op, StringComparer.Ordinal)) continue; // other rows (e.g. future ops) ignored by this authority path
            if (!expected.TryAdd(op, new A1Expected(op,
                    e.RootElement.GetProperty("cardinality").GetInt64(),
                    e.RootElement.GetProperty("digest").GetString() ?? "")))
                throw new InvalidDataException($"duplicate A1 operation {op}");
        }
        if (expected.Count != 8) throw new InvalidDataException("analytical-expected must contain exactly the eight A1-A5 operations");

        return new AnalyticalWorkload { Expected = expected };
    }

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }
}
