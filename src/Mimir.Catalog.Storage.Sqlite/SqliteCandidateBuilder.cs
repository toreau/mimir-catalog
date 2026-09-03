using System.Text.Json;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Candidate A builder: canonical Pass-B Parquet to a staging SQLite database,
/// A1 + physical validation, then atomic promotion. Ingestion uses genuinely
/// prepared, explicitly typed parameterized commands inside one explicit
/// transaction. Under journal_mode=OFF no transactional rollback is relied
/// upon; the staging DB is disposable recovery state.
/// </summary>
public static class SqliteCandidateBuilder
{
    public sealed class Report
    {
        public required string Verdict { get; set; } // OK / FAILED / HOLD
        public List<string> Reasons { get; } = new();
        public string? PublishedDir { get; set; }
        public string? StagingDir { get; set; }
    }

    /// <summary>Authoritative production entry point: synthetic mode is never selectable here.</summary>
    public static Report Run(SqliteBaselineConfig config, string corpusRoot, string workloadDir, string? candidatesRoot = null, string? runId = null)
        => RunCore(config, corpusRoot, workloadDir, candidatesRoot, runId, synthetic: false);

    internal static Report RunSynthetic(SqliteBaselineConfig config, string corpusRoot, string workloadDir, string? candidatesRoot = null, string? runId = null)
        => RunCore(config, corpusRoot, workloadDir, candidatesRoot, runId, synthetic: true);

    private static Report RunCore(SqliteBaselineConfig config, string corpusRoot, string workloadDir, string? candidatesRoot, string? runId, bool synthetic)
    {
        var report = new Report { Verdict = "HOLD" };
        runId ??= DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        string corpusId = config.CorpusIdValue;
        string root = candidatesRoot ?? ResolveCandidatesRoot(config, corpusRoot);
        string finalDir = Path.Combine(root, "sqlite-native-v1");
        if (Directory.Exists(finalDir))
        {
            report.Verdict = "FAILED";
            report.Reasons.Add($"published candidate already exists: {finalDir}");
            return report;
        }
        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, $"sqlite-native-v1-staging-{runId}");
        Directory.CreateDirectory(staging);

        try
        {
            var preflight = synthetic
                ? SqliteCandidatePreflight.RunSynthetic(config, corpusRoot, workloadDir)
                : SqliteCandidatePreflight.Run(config, corpusRoot, workloadDir);
            if (!preflight.Ok)
            {
                foreach (var r in preflight.Reasons) report.Reasons.Add(r);
                Abort(staging, report, runId, corpusId, "stage:preflight", report.Reasons);
                return report;
            }

            WriteState(staging, "Running", runId, corpusId);
            string dbPath = Path.Combine(staging, "candidate.db");

            // Ingestion: one explicit transaction; prepared, typed commands per relation.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                SqliteCandidateSchema.ApplyBuildPragmas(conn);
                SqliteCandidateSchema.CreateSchema(conn);
                using var tx = conn.BeginTransaction();
                IngestConcept(conn, tx, corpusRoot, preflight);
                IngestLexical(conn, tx, corpusRoot, preflight);
                IngestEdges(conn, tx, corpusRoot, "instance_of", "instance_of.parquet", preflight);
                IngestEdges(conn, tx, corpusRoot, "subclass_of", "subclass_of.parquet", preflight);
                tx.Commit();
            }

            // Indexes after ingestion, in their own explicit transaction, using the single DDL source.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                SqliteCandidateSchema.ApplyBuildPragmas(conn);
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = SqliteCandidateSchema.IndexesDdl;
                cmd.ExecuteNonQuery();
                tx.Commit();
            }

            var validation = SqliteCandidateValidator.Validate(dbPath, config, preflight.A1Expected);
            if (!validation.Ok)
            {
                foreach (var r in validation.Reasons) report.Reasons.Add(r);
                Abort(staging, report, runId, corpusId, "stage:validation", report.Reasons);
                return report;
            }

            var leftover = Directory.GetFiles(staging).Where(f => !IsExpectedFile(Path.GetFileName(f))).ToList();
            if (leftover.Count > 0)
            {
                report.Reasons.Add("unexpected files in staging: " + string.Join(",", leftover.Select(Path.GetFileName)));
                Abort(staging, report, runId, corpusId, "stage:leftover", report.Reasons);
                return report;
            }

            long bytes = new FileInfo(dbPath).Length;
            WriteSuccessEvidence(staging, corpusId, config, preflight, validation, bytes, runId);
            Directory.Move(staging, finalDir);

            report.Verdict = "OK";
            report.PublishedDir = finalDir;
            report.StagingDir = null;
            return report;
        }
        catch (Exception ex)
        {
            report.Reasons.Add($"build failure: {ex.Message}");
            Abort(staging, report, runId, corpusId, "stage:catch", report.Reasons);
            return report;
        }
    }

    private static bool IsExpectedFile(string name) => name is "candidate.db" or "build.json" or "build.state.json";

    private static void Abort(string staging, Report report, string runId, string corpusId, string stage, List<string> failedReasons)
    {
        try
        {
            foreach (var sidecar in Directory.GetFiles(staging).Where(f =>
                Path.GetFileName(f).StartsWith("candidate.db", StringComparison.Ordinal)))
                File.Delete(sidecar);
        }
        catch { /* best-effort */ }
        WriteState(staging, "Failed", runId, corpusId);
        WriteMinimalFailure(staging, stage, failedReasons);
        report.Verdict = "FAILED";
        report.StagingDir = staging;
    }

    // ---- genuinely prepared, explicitly typed ingestion ----

    private static void IngestConcept(SqliteConnection conn, SqliteTransaction tx, string corpusRoot, SqliteCandidatePreflight preflight)
    {
        string path = Path.Combine(corpusRoot, "pass-b", "concept.parquet");
        long count = 0;
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO concept (Qid, InT1, InT2) VALUES (@p0, @p1, @p2)";
            var pQid = cmd.Parameters.Add("@p0", SqliteType.Integer);
            var pIn1 = cmd.Parameters.Add("@p1", SqliteType.Integer);
            var pIn2 = cmd.Parameters.Add("@p2", SqliteType.Integer);
            cmd.Prepare();
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            var f2 = (DataField)reader.Schema.DataFields[2];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var qid = new long[n];
                var in1 = new bool[n];
                var in2 = new bool[n];
                Await(rg.ReadAsync<long>(f0, new Memory<long>(qid)));
                Await(rg.ReadAsync<bool>(f1, new Memory<bool>(in1)));
                Await(rg.ReadAsync<bool>(f2, new Memory<bool>(in2)));
                for (int i = 0; i < n; i++)
                {
                    pQid.Value = qid[i];
                    pIn1.Value = in1[i] ? 1L : 0L;
                    pIn2.Value = in2[i] ? 1L : 0L;
                    cmd.ExecuteNonQuery();
                    count++;
                }
            }
        }
        finally { Await(reader.DisposeAsync()); }
        EnsureRowCount("concept.parquet", count, preflight);
    }

    private static void IngestLexical(SqliteConnection conn, SqliteTransaction tx, string corpusRoot, SqliteCandidatePreflight preflight)
    {
        string path = Path.Combine(corpusRoot, "pass-b", "lexical_entry.parquet");
        long count = 0;
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO lexical_entry (Qid, Lang, LexKind, Value) VALUES (@p0, @p1, @p2, @p3)";
            var pQid = cmd.Parameters.Add("@p0", SqliteType.Integer);
            var pLang = cmd.Parameters.Add("@p1", SqliteType.Text);
            var pKind = cmd.Parameters.Add("@p2", SqliteType.Text);
            var pValue = cmd.Parameters.Add("@p3", SqliteType.Text);
            cmd.Prepare();
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            var f2 = (DataField)reader.Schema.DataFields[2];
            var f3 = (DataField)reader.Schema.DataFields[3];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var qid = new long[n];
                var lang = new string[n];
                var kind = new string[n];
                var value = new string[n];
                Await(rg.ReadAsync<long>(f0, new Memory<long>(qid)));
                Await(rg.ReadAsync(f1, new Memory<string>(lang)));
                Await(rg.ReadAsync(f2, new Memory<string>(kind)));
                Await(rg.ReadAsync(f3, new Memory<string>(value)));
                for (int i = 0; i < n; i++)
                {
                    pQid.Value = qid[i];
                    pLang.Value = lang[i];
                    pKind.Value = kind[i];
                    pValue.Value = value[i];
                    cmd.ExecuteNonQuery();
                    count++;
                }
            }
        }
        finally { Await(reader.DisposeAsync()); }
        EnsureRowCount("lexical_entry.parquet", count, preflight);
    }

    private static void IngestEdges(SqliteConnection conn, SqliteTransaction tx, string corpusRoot, string table, string file, SqliteCandidatePreflight preflight)
    {
        string path = Path.Combine(corpusRoot, "pass-b", file);
        long count = 0;
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {table} (SubjectQid, TargetQid) VALUES (@p0, @p1)";
            var pSub = cmd.Parameters.Add("@p0", SqliteType.Integer);
            var pTgt = cmd.Parameters.Add("@p1", SqliteType.Integer);
            cmd.Prepare();
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var sub = new long[n];
                var tgt = new long[n];
                Await(rg.ReadAsync<long>(f0, new Memory<long>(sub)));
                Await(rg.ReadAsync<long>(f1, new Memory<long>(tgt)));
                for (int i = 0; i < n; i++)
                {
                    pSub.Value = sub[i];
                    pTgt.Value = tgt[i];
                    cmd.ExecuteNonQuery();
                    count++;
                }
            }
        }
        finally { Await(reader.DisposeAsync()); }
        EnsureRowCount(file, count, preflight);
    }

    private static string OpForFile(string file) => file switch
    {
        "concept.parquet" => "A1-Concept",
        "lexical_entry.parquet" => "A1-LexicalEntry",
        "instance_of.parquet" => "A1-InstanceOf",
        "subclass_of.parquet" => "A1-SubclassOf",
        _ => throw new InvalidDataException(file),
    };

    private static void EnsureRowCount(string file, long actual, SqliteCandidatePreflight preflight)
    {
        long expected = preflight.A1Expected[OpForFile(file)].Cardinality;
        if (actual != expected)
            throw new InvalidDataException($"{file}: streamed rows {actual} != expected {expected}");
    }

    private static void Await(ValueTask t) => t.GetAwaiter().GetResult();
    private static void Await<T>(ValueTask<T> t) => t.GetAwaiter().GetResult();

    // ---- evidence ----
    private static void WriteState(string dir, string state, string runId, string corpusId)
    {
        WriteJson(Path.Combine(dir, "build.state.json"), w =>
        {
            w.WriteString("state", state);
            w.WriteString("run_id", runId);
            w.WriteString("corpus_id", corpusId);
            w.WriteString("utc", DateTime.UtcNow.ToString("O"));
        });
    }

    private static void WriteMinimalFailure(string dir, string stage, List<string> reasons)
    {
        WriteJson(Path.Combine(dir, "build.json"), w =>
        {
            w.WriteString("state", "Failed");
            w.WriteString("stage", stage);
            w.WriteString("reason", reasons.Count > 0 ? string.Join(" | ", reasons.Take(5)) : "unknown");
        });
    }

    private static void WriteSuccessEvidence(string dir, string corpusId, SqliteBaselineConfig config,
        SqliteCandidatePreflight preflight, SqliteCandidateValidator.Outcome validation, long bytes, string runId)
    {
        var (provider, sqliteLib) = SqliteCandidateSchema.RuntimeVersions();
        string parquetVersion = typeof(ParquetReader).Assembly.GetName().Version?.ToString() ?? "";
        WriteJson(Path.Combine(dir, "build.json"), w =>
        {
            w.WriteString("state", "Complete");
            w.WriteString("verdict", "OK");
            w.WriteString("run_id", runId);
            w.WriteString("corpus_id", corpusId);
            w.WriteString("workload_id", config.WorkloadIdValue);
            w.WriteString("candidate_config_id", config.ConfigId());
            w.WriteString("provider", provider);
            w.WriteString("sqlite_lib", sqliteLib);
            w.WriteString("parquet_net", parquetVersion);
            w.WriteNumber("candidate_db_bytes", bytes);
            w.WritePropertyName("inputs");
            w.WriteStartObject();
            foreach (var (file, (sha, expected, observed)) in preflight.InputParquet.OrderBy(k => k.Key))
            {
                w.WritePropertyName(file.Replace(".parquet", ""));
                w.WriteStartObject();
                w.WriteString("sha256", sha);
                w.WriteNumber("expected_row_count", expected);
                w.WriteNumber("observed_row_count", observed);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WritePropertyName("a1");
            w.WriteStartObject();
            foreach (var op in new[] { "A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf" })
            {
                var (card, digest) = validation.A1Actual[op];
                w.WritePropertyName(op);
                w.WriteStartObject();
                w.WriteNumber("cardinality", card);
                w.WriteString("digest", digest);
                w.WriteString("status", "VALID");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WritePropertyName("final_candidate_files");
            w.WriteStartArray();
            foreach (var f in new[] { "candidate.db", "build.json", "build.state.json" }) w.WriteStringValue(f);
            w.WriteEndArray();
        });
        WriteState(dir, "Complete", runId, corpusId);
    }

    private static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static string ResolveCandidatesRoot(SqliteBaselineConfig config, string corpusRoot)
    {
        string full = Path.GetFullPath(corpusRoot);
        string parent = Path.GetDirectoryName(full) ?? ".";
        string grand = Path.GetDirectoryName(parent) ?? ".";
        return Path.Combine(grand, "benchmarks", config.CorpusIdValue, "candidates");
    }
}
