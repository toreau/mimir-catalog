using Microsoft.Data.Sqlite;
using Mimir.Catalog.Workload;
using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

/// <summary>
/// Synthetic Pass-B corpus + published workload package for the Candidate A
/// builder. Writes small Parquet fixtures, Pass-C evidence, and an
/// analytical-expected file whose A1 entries are computed from the same rows
/// with the frozen MultisetFoldV1 semantics.
/// </summary>
public sealed class SqliteBuilderWorld : IDisposable
{
    public string Root { get; }
    public string CorpusRoot => Path.Combine(Root, "corpus");
    public string WorkloadDir => Path.Combine(Root, "workload-v1");
    public string CandidatesRoot => Path.Combine(Root, "candidates");
    public SqliteBaselineConfig Config { get; }
    public IReadOnlyDictionary<string, (long Cardinality, string Digest)> A1Expected { get; private set; } = new Dictionary<string, (long, string)>();

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

    public SqliteBuilderWorld(
        IReadOnlyList<(long Qid, bool InT1, bool InT2)> concept,
        IReadOnlyList<(long Qid, string Lang, string LexKind, string Value)> lexical,
        IReadOnlyList<(long Sub, long Tgt)> instance,
        IReadOnlyList<(long Sub, long Tgt)> subclass,
        bool corruptConceptParquet = false,
        bool wrongConceptSchema = false,
        bool wrongLexicalValueType = false,
        bool conceptNullableInT1 = false,
        Dictionary<string, (long Cardinality, string Digest)>? a1Overrides = null,
        bool omitAnalyticalManifestSha = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "mimir-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(CorpusRoot, "pass-b"));
        Directory.CreateDirectory(Path.Combine(CorpusRoot, "pass-c"));
        Config = SqliteBaselineConfig.Parse(File.ReadAllBytes(RepoRel(Path.Combine("benchmarks", "candidate-a-sqlite-v1.json"))));

        string conceptPath = Path.Combine(CorpusRoot, "pass-b", "concept.parquet");
        if (corruptConceptParquet) File.WriteAllBytes(conceptPath, new byte[] { 1, 2, 3, 4 });
        else if (wrongConceptSchema) WriteWrongConcept(conceptPath, concept);
        else WriteConcept(conceptPath, concept, conceptNullableInT1);
        WriteLexical(Path.Combine(CorpusRoot, "pass-b", "lexical_entry.parquet"), lexical, wrongLexicalValueType);
        WriteEdges(Path.Combine(CorpusRoot, "pass-b", "instance_of.parquet"), instance);
        WriteEdges(Path.Combine(CorpusRoot, "pass-b", "subclass_of.parquet"), subclass);

        // Pass-C evidence recorded from actual file hashes.
        var artifacts = new System.Text.Json.Nodes.JsonObject();
        foreach (var file in new[] { "concept.parquet", "lexical_entry.parquet", "instance_of.parquet", "subclass_of.parquet" })
        {
            string p = Path.Combine(CorpusRoot, "pass-b", file);
            artifacts[file.Replace(".parquet", "")] = new System.Text.Json.Nodes.JsonObject
            {
                ["sha256"] = SqliteCandidatePreflight.Sha256(p),
                ["bytes"] = new FileInfo(p).Length,
            };
        }
        var validation = new System.Text.Json.Nodes.JsonObject
        {
            ["completed"] = true,
            ["verdict"] = "GO",
            ["failedGates"] = new System.Text.Json.Nodes.JsonArray(),
            ["inputs"] = new System.Text.Json.Nodes.JsonObject { ["parquetArtifacts"] = artifacts },
        };
        File.WriteAllText(Path.Combine(CorpusRoot, "pass-c", "validation.json"), validation.ToJsonString());
        File.WriteAllText(Path.Combine(CorpusRoot, "pass-c", "validation.state.json"),
            "{\"state\":\"Complete\"}");

        // A1 expected digests from the exact rows.
        var a1 = new Dictionary<string, (long, string)>
        {
            ["A1-Concept"] = FoldConcept(concept),
            ["A1-LexicalEntry"] = FoldLexical(lexical),
            ["A1-InstanceOf"] = FoldEdges(instance),
            ["A1-SubclassOf"] = FoldEdges(subclass),
        };
        if (a1Overrides != null) foreach (var kv in a1Overrides) a1[kv.Key] = kv.Value;
        A1Expected = a1;

        Directory.CreateDirectory(WorkloadDir);
        string analyticalPath = Path.Combine(WorkloadDir, "analytical-expected.jsonl");
        using (var sw = new StreamWriter(analyticalPath))
        {
            foreach (var (op, (card, digest)) in a1)
                sw.WriteLine($"{{\"op\":\"{op}\",\"cardinality\":{card},\"digest\":\"{digest}\"}}");
        }
        string analyticalSha = SqliteCandidatePreflight.Sha256(analyticalPath);
        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["workload_id"] = "synthetic",
            ["corpus_id"] = "synthetic",
            ["files"] = new System.Text.Json.Nodes.JsonObject { ["analytical-expected.jsonl"] = omitAnalyticalManifestSha ? "0000" : analyticalSha },
        };
        File.WriteAllText(Path.Combine(WorkloadDir, "manifest.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(WorkloadDir, "workload.state.json"), "{\"state\":\"Complete\"}");
    }

    public SqliteCandidateBuilder.Report Build()
        => SqliteCandidateBuilder.RunSynthetic(Config, CorpusRoot, WorkloadDir, CandidatesRoot);

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* ignore */ }
    }

    // ---- row-set writers ----
    private static void FinishWriter(ParquetWriter writer)
    {
        writer.DisposeAsync().GetAwaiter().GetResult();
    }

    private static void WriteFieldLong(ParquetRowGroupWriter rg, DataField field, long[] values)
        => rg.WriteAsync<long>(field, new ReadOnlyMemory<long>(values), repetitionLevels: null, customMetadata: null, cancellationToken: default).GetAwaiter().GetResult();

    private static void WriteFieldBool(ParquetRowGroupWriter rg, DataField field, bool[] values)
        => rg.WriteAsync<bool>(field, new ReadOnlyMemory<bool>(values), repetitionLevels: null, customMetadata: null, cancellationToken: default).GetAwaiter().GetResult();

    private static void WriteFieldString(ParquetRowGroupWriter rg, DataField field, string[] values)
        => rg.WriteAsync(field, (IReadOnlyCollection<string>)values, repetitionLevels: null).GetAwaiter().GetResult();

    private static void WriteConcept(string path, IReadOnlyList<(long, bool, bool)> rows, bool nullableIn1)
    {
        using var fs = File.Create(path);
        var schema = new ParquetSchema(new DataField<long>("Qid"), new DataField<bool>("InT1", nullableIn1), new DataField<bool>("InT2"));
        var writer = ParquetWriter.CreateAsync(schema, fs, null, append: false, default).GetAwaiter().GetResult();
        try
        {
            using var rg = writer.CreateRowGroup();
            WriteFieldLong(rg, schema.DataFields[0], rows.Select(r => r.Item1).ToArray());
            WriteFieldBool(rg, schema.DataFields[1], rows.Select(r => r.Item2).ToArray());
            WriteFieldBool(rg, schema.DataFields[2], rows.Select(r => r.Item3).ToArray());
        }
        finally { FinishWriter(writer); }
    }

    private static void WriteWrongConcept(string path, IReadOnlyList<(long, bool, bool)> rows)
    {
        using var fs = File.Create(path);
        var schema = new ParquetSchema(new DataField<string>("Wrong"), new DataField<long>("Other"));
        var writer = ParquetWriter.CreateAsync(schema, fs, null, append: false, default).GetAwaiter().GetResult();
        try
        {
            using var rg = writer.CreateRowGroup();
            WriteFieldString(rg, schema.DataFields[0], rows.Select(_ => "x").ToArray());
            WriteFieldLong(rg, schema.DataFields[1], rows.Select(r => r.Item1).ToArray());
        }
        finally { FinishWriter(writer); }
    }

    private static void WriteLexical(string path, IReadOnlyList<(long, string, string, string)> rows, bool wrongValueType)
    {
        using var fs = File.Create(path);
        var schema = new ParquetSchema(new DataField<long>("Qid"), new DataField<string>("Lang", nullable: false), new DataField<string>("LexKind", nullable: false),
            wrongValueType ? new DataField<long>("Value") : new DataField<string>("Value", nullable: false));
        var writer = ParquetWriter.CreateAsync(schema, fs, null, append: false, default).GetAwaiter().GetResult();
        try
        {
            using var rg = writer.CreateRowGroup();
            WriteFieldLong(rg, schema.DataFields[0], rows.Select(r => r.Item1).ToArray());
            WriteFieldString(rg, schema.DataFields[1], rows.Select(r => r.Item2).ToArray());
            WriteFieldString(rg, schema.DataFields[2], rows.Select(r => r.Item3).ToArray());
            if (wrongValueType) WriteFieldLong(rg, schema.DataFields[3], rows.Select(r => (long)r.Item4.Length).ToArray());
            else WriteFieldString(rg, schema.DataFields[3], rows.Select(r => r.Item4).ToArray());
        }
        finally { FinishWriter(writer); }
    }

    private static void WriteEdges(string path, IReadOnlyList<(long, long)> rows)
    {
        using var fs = File.Create(path);
        var schema = new ParquetSchema(new DataField<long>("SubjectQid"), new DataField<long>("TargetQid"));
        var writer = ParquetWriter.CreateAsync(schema, fs, null, append: false, default).GetAwaiter().GetResult();
        try
        {
            using var rg = writer.CreateRowGroup();
            WriteFieldLong(rg, schema.DataFields[0], rows.Select(r => r.Item1).ToArray());
            WriteFieldLong(rg, schema.DataFields[1], rows.Select(r => r.Item2).ToArray());
        }
        finally { FinishWriter(writer); }
    }
    private static (long, string) FoldConcept(IReadOnlyList<(long, bool, bool)> rows)
    {
        var f = new MultisetFoldV1();
        foreach (var r in rows) f.Add(MultisetFoldV1.ConceptRow(r.Item1, r.Item2, r.Item3));
        return (f.Count, f.Digest());
    }

    private static (long, string) FoldLexical(IReadOnlyList<(long, string, string, string)> rows)
    {
        var f = new MultisetFoldV1();
        foreach (var r in rows) f.Add(MultisetFoldV1.LexicalRow(r.Item1, r.Item2, r.Item3, r.Item4));
        return (f.Count, f.Digest());
    }

    private static (long, string) FoldEdges(IReadOnlyList<(long, long)> rows)
    {
        var f = new MultisetFoldV1();
        foreach (var r in rows) f.Add(MultisetFoldV1.EdgeRow(r.Item1, r.Item2));
        return (f.Count, f.Digest());
    }

    public static long QueryLong(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        SqliteCandidateSchema.ApplyReadSettings(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
