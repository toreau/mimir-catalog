using System.Text.Json;
using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Strict read-only preflight for the Candidate A builder: frozen Candidate
/// Config, Pass-C state, canonical Parquet identities/schemas/nullability/row
/// counts, and the authoritative published workload expected-result identity.
/// Production validation is authoritative; synthetic fixtures use an internal
/// test-only seam.
/// </summary>
public sealed class SqliteCandidatePreflight
{
    public const string OfficialCorpusId = "511adb9ebd066f1d4d344b80171902d5";
    public const string OfficialWorkloadId = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc";
    public const string ExpectedAnalyticalSha = "d5f2fc916c7ffe4b1e68821bde6b914df6cc013500b22b2bc04f2f0ca402bbef";
    public const string ExpectedManifestSha = "02ca19be526ad76d42b4681d6680d899aa51f99f8eed755333dfdec366f5776e";
    public const string ExpectedConfigId = "76ee16b121946175aa17dda7dca6e8387bc95803736692459517f07800e1788a";

    public static readonly (string File, long Rows)[] ProductionRows =
    [
        ("concept.parquet", 7_403_488),
        ("lexical_entry.parquet", 7_121_880),
        ("instance_of.parquet", 3_202_468),
        ("subclass_of.parquet", 5_233_394),
    ];

    private sealed record ColumnSpec(string Name, string Kind, bool Nullable);

    private static readonly Dictionary<string, ColumnSpec[]> Schemas = new()
    {
        ["concept.parquet"] = new[]
        {
            new ColumnSpec("Qid", "INT64", false),
            new ColumnSpec("InT1", "BOOL", false),
            new ColumnSpec("InT2", "BOOL", false),
        },
        ["lexical_entry.parquet"] = new[]
        {
            new ColumnSpec("Qid", "INT64", false),
            new ColumnSpec("Lang", "UTF8", false),
            new ColumnSpec("LexKind", "UTF8", false),
            new ColumnSpec("Value", "UTF8", false),
        },
        ["instance_of.parquet"] = new[]
        {
            new ColumnSpec("SubjectQid", "INT64", false),
            new ColumnSpec("TargetQid", "INT64", false),
        },
        ["subclass_of.parquet"] = new[]
        {
            new ColumnSpec("SubjectQid", "INT64", false),
            new ColumnSpec("TargetQid", "INT64", false),
        },
    };

    public bool Ok { get; private set; }
    public List<string> Reasons { get; } = new();
    public Dictionary<string, (long Cardinality, string Digest)> A1Expected { get; } = new();
    public Dictionary<string, (string Sha256, long Expected, long Observed)> InputParquet { get; } = new();

    private readonly Dictionary<string, string> _shaCache = new();

    /// <summary>Each canonical Parquet file is hashed exactly once per preflight invocation.</summary>
    internal string Hash(string corpusRoot, string file)
    {
        if (_shaCache.TryGetValue(file, out var cached)) return cached;
        string sha = Sha256(Path.Combine(corpusRoot, "pass-b", file));
        _shaCache[file] = sha;
        return sha;
    }

    private void Fail(string m) => Reasons.Add(m);

    public static SqliteCandidatePreflight Run(SqliteBaselineConfig config, string corpusRoot, string workloadDir)
        => RunCore(config, corpusRoot, workloadDir, synthetic: false);

    internal static SqliteCandidatePreflight RunSynthetic(SqliteBaselineConfig config, string corpusRoot, string workloadDir)
        => RunCore(config, corpusRoot, workloadDir, synthetic: true);

    private static SqliteCandidatePreflight RunCore(SqliteBaselineConfig config, string corpusRoot, string workloadDir, bool synthetic)
    {
        var o = new SqliteCandidatePreflight();
        try
        {
            if (config.ConfigId() != ExpectedConfigId) { o.Fail("Candidate Config ID mismatch"); return o; }
            if (!synthetic && config.CorpusIdValue != OfficialCorpusId) { o.Fail("candidate config corpus id mismatch"); return o; }

            ValidatePassC(o, corpusRoot);
            if (o.Reasons.Count > 0) return o;

            ValidateWorkload(o, workloadDir, synthetic);
            if (o.Reasons.Count > 0) return o;

            ValidateParquet(o, corpusRoot, synthetic);
            if (o.Reasons.Count > 0) return o;

            o.Ok = true;
        }
        catch (Exception ex)
        {
            o.Fail($"preflight error: {ex}");
        }
        return o;
    }

    /// <summary>Internal check used by authoritative validation; test-only harness may call with synthetic expectations.</summary>
    internal static List<string> VerifyRowCounts(string corpusRoot, IReadOnlyDictionary<string, long> expected)
    {
        var reasons = new List<string>();
        foreach (var (file, rows) in expected)
        {
            long actual = Inspect(Path.Combine(corpusRoot, "pass-b", file)).RowCount;
            if (actual != rows) reasons.Add($"{file}: row count {actual} != expected {rows}");
        }
        return reasons;
    }

    private static void ValidatePassC(SqliteCandidatePreflight o, string corpusRoot)
    {
        string passC = Path.Combine(corpusRoot, "pass-c");
        string statePath = Path.Combine(passC, "validation.state.json");
        string jsonPath = Path.Combine(passC, "validation.json");
        if (!File.Exists(statePath) || !File.Exists(jsonPath)) { o.Fail("pass-c evidence missing"); return; }

        using (var st = JsonDocument.Parse(File.ReadAllBytes(statePath)))
        {
            if (!st.RootElement.TryGetProperty("state", out var s) || s.GetString() != "Complete")
            { o.Fail("pass-c state != Complete"); return; }
        }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("completed", out var co) || co.ValueKind != JsonValueKind.True) { o.Fail("validation.json completed != true"); return; }
        if (!root.TryGetProperty("verdict", out var vd) || vd.GetString() != "GO") { o.Fail("validation.json verdict != GO"); return; }
        if (!root.TryGetProperty("failedGates", out var fg) || fg.ValueKind != JsonValueKind.Array || fg.GetArrayLength() != 0) { o.Fail("failedGates not empty"); return; }
        if (!root.TryGetProperty("inputs", out var inp) || inp.ValueKind != JsonValueKind.Object
            || !inp.TryGetProperty("parquetArtifacts", out var arts) || arts.ValueKind != JsonValueKind.Object)
        { o.Fail("validation.json inputs.parquetArtifacts missing"); return; }

        foreach (var file in Schemas.Keys)
        {
            string recorded = arts.TryGetProperty(file.Replace(".parquet", ""), out var af)
                && af.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : "";
            string actual = o.Hash(corpusRoot, file);
            if (string.IsNullOrEmpty(recorded) || !string.Equals(recorded, actual, StringComparison.Ordinal))
                o.Fail($"Parquet identity mismatch for {file}");
        }
    }

    private static void ValidateWorkload(SqliteCandidatePreflight o, string workloadDir, bool synthetic)
    {
        string manifestPath = Path.Combine(workloadDir, "manifest.json");
        string statePath = Path.Combine(workloadDir, "workload.state.json");
        string analytical = Path.Combine(workloadDir, "analytical-expected.jsonl");
        if (!File.Exists(manifestPath) || !File.Exists(statePath) || !File.Exists(analytical))
        { o.Fail("published workload package incomplete"); return; }

        using var st = JsonDocument.Parse(File.ReadAllBytes(statePath));
        if (!st.RootElement.TryGetProperty("state", out var s) || s.GetString() != "Complete")
        { o.Fail("workload publication state != Complete"); return; }

        string manifestSha = Sha256(manifestPath);
        if (!synthetic && manifestSha != ExpectedManifestSha) { o.Fail("authoritative workload manifest identity mismatch"); return; }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var m = doc.RootElement;
        string wid = m.TryGetProperty("workload_id", out var w) ? w.GetString() ?? "" : "";
        string corpus = m.TryGetProperty("corpus_id", out var c) ? c.GetString() ?? "" : "";
        if (!synthetic && (wid != OfficialWorkloadId || corpus != OfficialCorpusId))
        { o.Fail("workload manifest identity mismatch"); return; }

        string analyticalSha = Sha256(analytical);
        string manifestShaFile = m.TryGetProperty("files", out var files) && files.TryGetProperty("analytical-expected.jsonl", out var fsha)
            ? fsha.GetString() ?? "" : "";
        if (manifestShaFile != analyticalSha) { o.Fail("analytical-expected manifest identity mismatch"); return; }
        if (!synthetic && analyticalSha != ExpectedAnalyticalSha) { o.Fail("analytical-expected identity mismatch"); return; }

        foreach (var line in File.ReadLines(analytical))
        {
            using var e = JsonDocument.Parse(line);
            string op = e.RootElement.GetProperty("op").GetString()!;
            if (op.StartsWith("A1-", StringComparison.Ordinal))
            {
                long card = e.RootElement.GetProperty("cardinality").GetInt64();
                string digest = e.RootElement.GetProperty("digest").GetString()!;
                o.A1Expected[op] = (card, digest);
            }
        }
        if (o.A1Expected.Count != 4)
            o.Fail("analytical-expected must contain exactly the four A1 operations");
    }

    private static void ValidateParquet(SqliteCandidatePreflight o, string corpusRoot, bool synthetic)
    {
        foreach (var (file, spec) in Schemas)
        {
            string path = Path.Combine(corpusRoot, "pass-b", file);
            InspectionResult inspect;
            try { inspect = Inspect(path); }
            catch (Exception ex) { o.Fail($"cannot read Parquet {file}: {ex.Message}"); return; }

            if (inspect.Columns.Length != spec.Length) { o.Fail($"{file}: field count mismatch"); continue; }
            for (int i = 0; i < spec.Length; i++)
            {
                var got = inspect.Columns[i];
                var want = spec[i];
                if (got.Name != want.Name) { o.Fail($"{file}: field {i} name {got.Name} != {want.Name}"); continue; }
                if (got.Kind != want.Kind) { o.Fail($"{file}: field {i} type {got.Kind} != {want.Kind}"); continue; }
                if (got.Nullable != want.Nullable) { o.Fail($"{file}: field {i} nullability mismatch"); continue; }
            }

            long expected = synthetic ? o.A1Expected[OpForFile(file)].Cardinality : ProductionRows.Single(r => r.File == file).Rows;
            if (inspect.RowCount != expected) { o.Fail($"{file}: row count {inspect.RowCount} != expected {expected}"); continue; }

            o.InputParquet[file] = (o.Hash(corpusRoot, file), expected, inspect.RowCount);
        }
    }

    private static string OpForFile(string file) => file switch
    {
        "concept.parquet" => "A1-Concept",
        "lexical_entry.parquet" => "A1-LexicalEntry",
        "instance_of.parquet" => "A1-InstanceOf",
        "subclass_of.parquet" => "A1-SubclassOf",
        _ => throw new InvalidDataException(file),
    };

    private sealed record InspectionResult(long RowCount, ColumnRead[] Columns);
    private sealed record ColumnRead(string Name, string Kind, bool Nullable);

    /// <summary>
    /// Exact kind classification. UTF8 is recognized only for the concrete
    /// Parquet.Net string representation (string, or ReadOnlyMemory whose
    /// element type is char); any other ReadOnlyMemory&lt;T&gt; stays distinct.
    /// </summary>
    internal static string KindOfType(Type clr)
    {
        if (clr == typeof(long)) return "INT64";
        if (clr == typeof(bool)) return "BOOL";
        bool charMemory = clr.IsGenericType
            && clr.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>)
            && clr.GetGenericArguments()[0] == typeof(char);
        if (clr == typeof(string) || charMemory) return "UTF8";
        return clr.Name;
    }

    private static InspectionResult Inspect(string path)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            long rows = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                rows += rg.RowCount;
            }
            var cols = reader.Schema.DataFields.Select(df =>
            {
                var d = (DataField)df;
                return new ColumnRead(d.Name, KindOfType(d.ClrType), d.IsNullable);
            }).ToArray();
            return new InspectionResult(rows, cols);
        }
        finally { reader.DisposeAsync().GetAwaiter().GetResult(); }
    }

    /// <summary>Streaming SHA-256 (never loads whole files).</summary>
    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }
}
