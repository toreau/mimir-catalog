using System.Diagnostics;
using System.Text.Json;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Workload identity (content-addressed, full 64-hex lowercase SHA-256 over the
/// canonical semantic contract image plus input identities) and the
/// authoritative generation runner (preflight, load, build, publish).
/// </summary>
public static class WorkloadIdentity
{
    public static string Compute(WorkloadContractV1 c, string corpusId, IReadOnlyList<(string Name, string Sha256)> corpusArtifacts, string fixtureSha256)
    {
        var b = new Canon.Builder();
        b.AddRaw(c.CanonicalNormative());
        b.AddString(corpusId);
        foreach (var a in corpusArtifacts.OrderBy(a => a.Name, StringComparer.Ordinal))
            b.AddString(a.Name).AddString(a.Sha256);
        b.AddString(fixtureSha256);
        return b.ToSha256Hex();
    }
}

public sealed class RunReport
{
    public required string Verdict { get; set; }
    public List<string> Reasons { get; } = new();
    public string? WorkloadId { get; set; }
    public string? PublishedDir { get; set; }
    public Dictionary<string, long> PoolCardinalities { get; } = new();
    public Dictionary<string, object> Continuity { get; } = new();
    public long MeasuredServingCount { get; set; }
    public long MeasuredG1Count { get; set; }
    public int G2BatchCount { get; set; }
    public double WallSeconds { get; set; }
    public string? CorpusId { get; set; }
    public Dictionary<string, string> FileSha256 { get; } = new();
    public long ManagedBytes { get; set; }
    public int G1CandidatesConsidered { get; set; }
    public int G1RejectedGuard { get; set; }
    public long G1MaxVisited { get; set; }
    public int G2CandidatesConsidered { get; set; }
    public int G2RejectedGuard { get; set; }
    public int G2Accepted { get; set; }
    public long G2MaxVisited { get; set; }
    public string? MachineContractPath { get; set; }
    public string? MachineContractSha { get; set; }
    public string? FixtureSha { get; set; }
}

public sealed class PreflightOutcome
{
    public bool Ok { get; set; }
    public List<string> Reasons { get; } = new();
    public List<(string Name, string Sha256)> ArtifactShas { get; } = new();
    public string? FixtureSha256 { get; set; }
}

/// <summary>Authoritative Phase 1.1A.3 workload generation entry point.</summary>
public static class WorkloadRun
{
    public const string Go = "GO";
    public const string Hold = "HOLD";
    public const string DefaultContractPath = "benchmarks/workload-contract-v1.json";

    public static readonly (string Relation, string File, long Rows)[] FrozenRowCounts =
    {
        ("Concept", "concept.parquet", 7_403_488),
        ("LexicalEntry", "lexical_entry.parquet", 7_121_880),
        ("InstanceOf", "instance_of.parquet", 3_202_468),
        ("SubclassOf", "subclass_of.parquet", 5_233_394),
    };

    public static RunReport Run(string corpusRoot, string fixturePath, string? explicitOutRoot = null, string? contractPath = null)
    {
        var sw = Stopwatch.StartNew();
        var report = new RunReport { Verdict = Go };
        string corpusId = Path.GetFileName(corpusRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        report.CorpusId = corpusId;

        string contractFile = contractPath ?? DefaultContractPath;
        WorkloadContractV1 contract;
        try
        {
            byte[] raw = File.ReadAllBytes(contractFile);
            contract = WorkloadContractV1.Parse(raw);
            report.MachineContractPath = Path.GetFullPath(contractFile);
            report.MachineContractSha = Canon.Sha256Hex(raw);
        }
        catch (Exception ex)
        {
            report.Reasons.Add($"machine contract load failed: {ex.Message}");
            return Finalize(report, Hold, sw);
        }

        // ---- preflight ----
        var preflight = CheckPreflight(corpusRoot, fixturePath);
        report.FixtureSha = preflight.FixtureSha256;
        if (!preflight.Ok)
        {
            foreach (var r in preflight.Reasons) report.Reasons.Add(r);
            return Finalize(report, Hold, sw);
        }

        string passB = Path.Combine(corpusRoot, "pass-b");
        var concept = ParquetLoader.LoadConcept(passB);
        var lexical = ParquetLoader.LoadLexical(passB);
        var instance = ParquetLoader.LoadEdge("InstanceOf", passB);
        var subclass = ParquetLoader.LoadEdge("SubclassOf", passB);

        foreach (var frozen in FrozenRowCounts)
        {
            long actual = frozen.Relation switch
            {
                "Concept" => concept.Total,
                "LexicalEntry" => lexical.RowCount,
                "InstanceOf" => instance.RowCount,
                "SubclassOf" => subclass.RowCount,
                _ => 0,
            };
            if (actual != frozen.Rows)
            {
                report.Reasons.Add($"{frozen.Relation}: row count {actual} != frozen {frozen.Rows}");
                return Finalize(report, Hold, sw);
            }
        }

        string fixtureSha = Canon.Sha256Hex(File.ReadAllBytes(fixturePath));
        string workloadId = WorkloadIdentity.Compute(contract, corpusId, preflight.ArtifactShas, fixtureSha);
        report.WorkloadId = workloadId;

        var build = WorkloadBuild.Build(
            contract, corpusId, concept, lexical, instance, subclass,
            () => ParquetLoader.EnumerateLexical(passB),
            fixturePath);

        foreach (var r in build.Reasons) report.Reasons.Add(r);
        foreach (var kv in build.PoolCardinalities) report.PoolCardinalities[kv.Key] = kv.Value;
        foreach (var kv in build.Continuity) report.Continuity[kv.Key] = kv.Value;
        report.MeasuredServingCount = build.MeasuredServingCount;
        report.MeasuredG1Count = build.MeasuredG1Count;
        report.G2BatchCount = build.G2BatchCount;
        report.G1CandidatesConsidered = build.G1CandidatesConsidered;
        report.G1RejectedGuard = build.G1RejectedGuard;
        report.G1MaxVisited = build.G1MaxVisited;
        report.G2CandidatesConsidered = build.G2CandidatesConsidered;
        report.G2RejectedGuard = build.G2RejectedGuard;
        report.G2Accepted = build.G2Accepted;
        report.G2MaxVisited = build.G2MaxVisited;

        if (build.Verdict == Hold)
        {
            return Finalize(report, Hold, sw);
        }

        var package = WorkloadPackageValidator.Validate(
            contract, build.ServingLines!, build.GraphLines!, build.ExpectedLines!, build.AnalyticalLines!);
        if (!package.Ok)
        {
            foreach (var r in package.Reasons) report.Reasons.Add($"package gate: {r}");
            return Finalize(report, Hold, sw);
        }

        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in preflight.ArtifactShas) provenance[a.Name] = a.Sha256;
        provenance["phase0_fixture"] = preflight.FixtureSha256!;
        provenance["machine_contract"] = report.MachineContractSha!;

        var benchmarkRoot = ResolveBenchmarkRoot(corpusRoot, explicitOutRoot);
        var publisher = new WorkloadPublisher(benchmarkRoot, corpusId, workloadId);
        var publishResult = publisher.Publish(
            contract,
            build.ServingLines!, build.GraphLines!, build.ExpectedLines!, build.AnalyticalLines!,
            build.PoolCardinalities, build.Continuity,
            report.MeasuredServingCount, report.MeasuredG1Count, build.G2BatchCount,
            build.G1CandidatesConsidered, build.G1RejectedGuard, build.G1MaxVisited,
            build.G2CandidatesConsidered, build.G2RejectedGuard, build.G2MaxVisited,
            provenance);
        foreach (var r in publishResult.Reasons) report.Reasons.Add(r);
        if (!publishResult.Ok)
        {
            return Finalize(report, Hold, sw);
        }
        foreach (var kv in publishResult.FileSha256) report.FileSha256[kv.Key] = kv.Value;
        report.PublishedDir = publishResult.PublishedDir;
        return Finalize(report, Go, sw);
    }

    private static RunReport Finalize(RunReport r, string verdict, Stopwatch sw)
    {
        r.Verdict = verdict;
        r.WallSeconds = sw.Elapsed.TotalSeconds;
        r.ManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
        return r;
    }

    private static string ResolveBenchmarkRoot(string corpusRoot, string? explicitRoot)
    {
        if (!string.IsNullOrEmpty(explicitRoot)) return Path.GetFullPath(explicitRoot);
        string full = Path.GetFullPath(corpusRoot);
        string parent = Path.GetDirectoryName(full) ?? ".";
        string grand = Path.GetDirectoryName(parent) ?? ".";
        return Path.Combine(grand, "benchmarks", Path.GetFileName(full));
    }

    /// <summary>Strict read-only preflight against accepted Pass-C evidence.</summary>
    public static PreflightOutcome CheckPreflight(string corpusRoot, string fixturePath)
    {
        var r = new PreflightOutcome();
        if (!Directory.Exists(corpusRoot)) { r.Reasons.Add($"corpus root not found: {corpusRoot}"); return r; }

        string passC = Path.Combine(corpusRoot, "pass-c");
        string statePath = Path.Combine(passC, "validation.state.json");
        string jsonPath = Path.Combine(passC, "validation.json");
        if (!File.Exists(statePath) || !File.Exists(jsonPath))
        {
            r.Reasons.Add("pass-c validation evidence missing (validation.state.json / validation.json)");
            return r;
        }

        try
        {
            using var st = JsonDocument.Parse(File.ReadAllBytes(statePath));
            var rootSt = st.RootElement;
            if (!rootSt.TryGetProperty("state", out var stateProp))
            {
                r.Reasons.Add("pass-c validation.state.json missing 'state' property");
                return r;
            }
            if (stateProp.ValueKind != JsonValueKind.String)
            {
                r.Reasons.Add("pass-c validation.state.json 'state' is malformed (not a string)");
                return r;
            }
            if (stateProp.GetString() != "Complete")
            {
                r.Reasons.Add($"pass-c state != Complete (got {stateProp.GetString()})");
                return r;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("completed", out var co) || co.ValueKind != JsonValueKind.True)
            {
                r.Reasons.Add("pass-c validation.json completed must be true");
                return r;
            }
            if (!root.TryGetProperty("verdict", out var vd) || vd.GetString() != "GO")
            {
                r.Reasons.Add("pass-c validation.json verdict must be GO");
                return r;
            }
            if (!root.TryGetProperty("failedGates", out var fg) || fg.ValueKind != JsonValueKind.Array || fg.GetArrayLength() != 0)
            {
                r.Reasons.Add("pass-c validation.json failedGates must be an empty array");
                return r;
            }

            var inputs = root.TryGetProperty("inputs", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp : default;
            if (inputs.ValueKind != JsonValueKind.Object)
            {
                r.Reasons.Add("validation.json inputs missing");
                return r;
            }

            string t2Path = Path.Combine(corpusRoot, "pass-a", "t2-endpoints.bin");
            string t2Actual = Canon.Sha256Hex(File.ReadAllBytes(t2Path));
            string? t2Rec = inputs.TryGetProperty("t2", out var t2) && t2.TryGetProperty("sha256", out var t2sha) && t2sha.ValueKind == JsonValueKind.String ? t2sha.GetString() : null;
            if (t2Rec == null || !string.Equals(t2Rec, t2Actual, StringComparison.Ordinal))
            {
                r.Reasons.Add("pass-c recorded T2 SHA does not match pass-a/t2-endpoints.bin");
                return r;
            }
            r.ArtifactShas.Add(("t2", t2Actual));

            var arts = inputs.TryGetProperty("parquetArtifacts", out var pa) && pa.ValueKind == JsonValueKind.Object ? pa : default;
            foreach (var frozen in FrozenRowCounts)
            {
                string parquetPath = Path.Combine(corpusRoot, "pass-b", frozen.File);
                string actualSha = Canon.Sha256Hex(File.ReadAllBytes(parquetPath));
                string key = frozen.File.Replace(".parquet", "");
                string? recorded = arts.ValueKind == JsonValueKind.Object && arts.TryGetProperty(key, out var af)
                    && af.TryGetProperty("sha256", out var ash) && ash.ValueKind == JsonValueKind.String ? ash.GetString() : null;
                if (recorded == null || !string.Equals(recorded, actualSha, StringComparison.Ordinal))
                {
                    r.Reasons.Add($"pass-c recorded {frozen.File} SHA does not match pass-b artifact");
                    return r;
                }
                r.ArtifactShas.Add((frozen.File, actualSha));
            }

            if (!File.Exists(fixturePath))
            {
                r.Reasons.Add($"phase-0 fixture not found: {fixturePath}");
                return r;
            }
            string fixtureActual = Canon.Sha256Hex(File.ReadAllBytes(fixturePath));
            r.FixtureSha256 = fixtureActual;
            string? fixtureRecorded = inputs.TryGetProperty("phase0Fixture", out var pf)
                && pf.ValueKind == JsonValueKind.Object
                && pf.TryGetProperty("sha256", out var fsha)
                && fsha.ValueKind == JsonValueKind.String ? fsha.GetString() : null;
            if (fixtureRecorded == null)
            {
                r.Reasons.Add("pass-c validation.json inputs.phase0Fixture.sha256 missing or malformed");
                return r;
            }
            if (!string.Equals(fixtureRecorded, fixtureActual, StringComparison.Ordinal))
            {
                r.Reasons.Add($"phase-0 fixture SHA mismatch: file {fixtureActual}, Pass-C recorded {fixtureRecorded}");
                return r;
            }
        }
        catch (Exception ex)
        {
            r.Reasons.Add($"preflight parse error: {ex.Message}");
            return r;
        }

        r.Ok = true;
        return r;
    }
}
