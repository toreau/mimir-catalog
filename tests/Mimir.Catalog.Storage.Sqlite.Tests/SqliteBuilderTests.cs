using System.Text.Json;
using Mimir.Catalog.Storage.Sqlite;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

public class SqliteBuilderTests
{
    private static readonly (long Qid, bool InT1, bool InT2)[] Concept =
    [
        (1, true, false), (2, false, true), (3, true, true),
    ];

    private static readonly (long Qid, string Lang, string LexKind, string Value)[] Lexical =
    [
        (1, "en", "label", "Alpha"),
        (1, "en", "label", "alpha"),
        (1, "en", "label", "\u00e9"),
        (1, "en", "label", "e\u0301"),
        (2, "nb", "alias", "dup"),
        (2, "nb", "alias", "dup"),
    ];

    private static readonly (long Sub, long Tgt)[] Instance =
    [
        (1, 100), (1, 100), (2, 200),
    ];

    private static readonly (long Sub, long Tgt)[] Subclass =
    [
        (1, 10), (2, 20), (3, 30),
    ];

    private static SqliteBuilderWorld OkWorld()
        => new(Concept, Lexical, Instance, Subclass);

    [Fact]
    public void Build_Ok_AllFourRelations_ValidatedAndPromoted()
    {
        using var w = OkWorld();
        var pre = SqliteCandidatePreflight.RunSynthetic(w.Config, w.CorpusRoot, w.WorkloadDir);
        if (!pre.Ok) throw new Xunit.Sdk.XunitException("preflight: " + string.Join(" | ", pre.Reasons));
        var report = w.Build();
        if (report.Verdict != "OK")
        {
            string detail = report.Reasons.Count > 0 ? string.Join(" | ", report.Reasons) : "(no reasons)";
            if (report.StagingDir != null && File.Exists(Path.Combine(report.StagingDir, "build.json")))
                detail += " build.json=" + File.ReadAllText(Path.Combine(report.StagingDir, "build.json"));
            throw new Xunit.Sdk.XunitException("build failed: " + detail);
        }
        string final = Path.Combine(w.CandidatesRoot, "sqlite-native-v1");
        Assert.True(Directory.Exists(final));
        var files = Directory.GetFiles(final).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "build.json", "build.state.json", "candidate.db" }, files);
        Assert.Equal(3L, SqliteBuilderWorld.QueryLong(Path.Combine(final, "candidate.db"), "SELECT COUNT(*) FROM concept"));
        Assert.Equal(1L, SqliteBuilderWorld.QueryLong(Path.Combine(final, "candidate.db"), "SELECT InT1 FROM concept WHERE Qid=1"));
        Assert.Equal(0L, SqliteBuilderWorld.QueryLong(Path.Combine(final, "candidate.db"), "SELECT InT2 FROM concept WHERE Qid=1"));
        Assert.Equal(1L, SqliteBuilderWorld.QueryLong(Path.Combine(final, "candidate.db"), "SELECT InT2 FROM concept WHERE Qid=2"));
    }

    [Fact]
    public void Build_NoWalShmSidecars()
    {
        using var w = OkWorld();
        var report = w.Build();
        Assert.Equal("OK", report.Verdict);
        var names = Directory.GetFiles(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")).Select(Path.GetFileName).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("-wal") || n.Contains("-shm") || n.EndsWith(".db-journal"));
    }

    [Fact]
    public void Duplicates_LexicalAndEdges_Preserved()
    {
        using var w = OkWorld();
        var report = w.Build();
        Assert.Equal("OK", report.Verdict);
        string db = Path.Combine(w.CandidatesRoot, "sqlite-native-v1", "candidate.db");
        Assert.Equal(2L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM lexical_entry WHERE Qid=2 AND Value='dup'"));
        Assert.Equal(2L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM instance_of WHERE SubjectQid=1"));
    }

    [Fact]
    public void Lexical_CaseAndRawUnicode_Survive()
    {
        using var w = OkWorld();
        var report = w.Build();
        Assert.Equal("OK", report.Verdict);
        string db = Path.Combine(w.CandidatesRoot, "sqlite-native-v1", "candidate.db");
        Assert.Equal(1L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM lexical_entry WHERE Value='Alpha'"));
        Assert.Equal(0L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM lexical_entry WHERE Value='ALPHA'"));
        Assert.Equal(1L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM lexical_entry WHERE Value='\u00e9'"));
        Assert.Equal(1L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM lexical_entry WHERE Value='e\u0301'"));
        Assert.Equal(6L, SqliteBuilderWorld.QueryLong(db, "SELECT COUNT(*) FROM lexical_entry"));
    }

    [Fact]
    public void DuplicateConceptQid_FailsWithoutPromotion()
    {
        using var w = new SqliteBuilderWorld(
            new[] { (1L, true, false), (1L, false, true) }, Lexical, Instance, Subclass);
        var report = w.Build();
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
        Assert.NotNull(report.StagingDir);
        AssertState(report.StagingDir!, "Failed");
        Assert.Empty(Directory.GetFiles(report.StagingDir!).Where(f => Path.GetFileName(f).StartsWith("candidate.db")));
    }

    [Fact]
    public void WrongConceptSchema_Fails()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass, wrongConceptSchema: true);
        var report = w.Build();
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void CorruptParquet_Fails()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass, corruptConceptParquet: true);
        var report = w.Build();
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void RowCountMismatch_Fails()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass, a1Overrides: new()
        {
            ["A1-Concept"] = (Concept.Length + 1, "00".PadRight(64, '0')),
        });
        var report = w.Build();
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void DigestMismatch_FailsWithoutPromotion()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass, a1Overrides: new()
        {
            ["A1-Concept"] = (Concept.Length, "11".PadRight(64, '1')),
        });
        var report = w.Build();
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void InvalidWorkloadExpectedArtifact_PreventsAcceptance()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass, omitAnalyticalManifestSha: true);
        var report = w.Build();
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
        AssertState(report.StagingDir!, "Failed");
    }

    [Fact]
    public void ExistingPublishedCandidate_NeverOverwritten()
    {
        using var w = OkWorld();
        Assert.Equal("OK", w.Build().Verdict);
        var second = w.Build();
        Assert.Equal("FAILED", second.Verdict);
        Assert.Contains(second.Reasons, r => r.Contains("already exists"));
        Assert.Single(Directory.GetDirectories(w.CandidatesRoot));
    }

    [Fact]
    public void ConfigMismatch_AbortsBeforePublication()
    {
        using var w = OkWorld();
        var wrongConfig = new SqliteBaselineConfig { BuildJournalMode = "DELETE" };
        var report = SqliteCandidateBuilder.RunSynthetic(wrongConfig, w.CorpusRoot, w.WorkloadDir, w.CandidatesRoot);
        Assert.Equal("FAILED", report.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void Validator_RejectsMissingIndex_OnTamperedCopy()
    {
        using var w = OkWorld();
        Assert.Equal("OK", w.Build().Verdict);
        string db = Path.Combine(w.CandidatesRoot, "sqlite-native-v1", "candidate.db");
        string copy = Path.Combine(w.Root, "tampered.db");
        File.Copy(db, copy);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP INDEX lex_lang_value";
            cmd.ExecuteNonQuery();
        }
        var a1 = new Dictionary<string, (long, string)>();
        var outcome = SqliteCandidateValidator.Validate(copy, w.Config, w.A1Expected);
        Assert.False(outcome.Ok);
        Assert.Contains(outcome.Reasons, r => r.Contains("lex_lang_value"));
    }

    private static void AssertState(string staging, string expected)
    {
        string statePath = Path.Combine(staging, "build.state.json");
        Assert.True(File.Exists(statePath));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(statePath));
        Assert.Equal(expected, doc.RootElement.GetProperty("state").GetString());
    }
}

public class SqliteBuilderHardeningTests
{
    private static readonly (long Qid, bool InT1, bool InT2)[] Concept =
    [
        (1, true, false), (2, false, true),
    ];
    private static readonly (long Qid, string Lang, string LexKind, string Value)[] Lexical =
    [
        (1, "en", "label", "Alpha"),
    ];
    private static readonly (long Sub, long Tgt)[] Instance = [(1, 100)];
    private static readonly (long Sub, long Tgt)[] Subclass = [(1, 10)];

    private static SqliteCandidateBuilder.Report BuildWorld(params Action<SqliteBuilderWorld>[] configure)
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass);
        foreach (var c in configure) c(w);
        var r = w.Build();
        return r;
    }

    [Fact]
    public void WrongParquetType_Rejected()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass, wrongLexicalValueType: true);
        var r = w.Build();
        Assert.Equal("FAILED", r.Verdict);
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void PreflightRowCountMismatch_Rejected()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass);
        var reasons = SqliteCandidatePreflight.VerifyRowCounts(w.CorpusRoot, new Dictionary<string, long>
        {
            ["concept.parquet"] = Concept.Length + 1,
            ["lexical_entry.parquet"] = Lexical.Length,
            ["instance_of.parquet"] = Instance.Length,
            ["subclass_of.parquet"] = Subclass.Length,
        });
        Assert.Contains(reasons, r => r.Contains("concept.parquet: row count"));
    }

    [Fact]
    public void AuthoritativePublicRun_RejectsSyntheticWorkload()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass);
        var r = SqliteCandidateBuilder.Run(w.Config, w.CorpusRoot, w.WorkloadDir, w.CandidatesRoot);
        Assert.Equal("FAILED", r.Verdict);
        Assert.Contains(r.Reasons, reason => reason.Contains("workload") || reason.Contains("analytical"));
        Assert.False(Directory.Exists(Path.Combine(w.CandidatesRoot, "sqlite-native-v1")));
    }

    [Fact]
    public void PublicApi_ExposesNoSyntheticSwitch()
    {
        var overloads = typeof(SqliteCandidateBuilder).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == "Run").ToArray();
        Assert.NotEmpty(overloads);
        foreach (var m in overloads)
            Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(bool) && p.Name == "synthetic");
    }

    [Fact]
    public void SuccessEvidence_Complete()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass);
        Assert.Equal("OK", w.Build().Verdict);
        string buildJson = Path.Combine(w.CandidatesRoot, "sqlite-native-v1", "build.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(buildJson));
        var root = doc.RootElement;
        Assert.Equal("511adb9ebd066f1d4d344b80171902d5", root.GetProperty("corpus_id").GetString());
        Assert.True(root.GetProperty("candidate_db_bytes").GetInt64() > 0);
        var inputs = root.GetProperty("inputs");
        foreach (var key in new[] { "concept", "lexical_entry", "instance_of", "subclass_of" })
        {
            var e = inputs.GetProperty(key);
            Assert.Equal(64, e.GetProperty("sha256").GetString()!.Length);
            Assert.True(e.GetProperty("expected_row_count").GetInt64() == e.GetProperty("observed_row_count").GetInt64());
        }
        var files = root.GetProperty("final_candidate_files").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "candidate.db", "build.json", "build.state.json" }, files);
    }

    [Fact]
    public void Validator_ReadOnlyPath_Succeeds()
    {
        using var w = new SqliteBuilderWorld(Concept, Lexical, Instance, Subclass);
        Assert.Equal("OK", w.Build().Verdict);
        string db = Path.Combine(w.CandidatesRoot, "sqlite-native-v1", "candidate.db");
        var outcome = SqliteCandidateValidator.Validate(db, w.Config, w.A1Expected);
        Assert.True(outcome.Ok, string.Join(";", outcome.Reasons));
    }
}

public class SqliteBuilderMicroCloseoutTests
{
    [Fact]
    public void FailedState_PreservesSemanticCorpusId()
    {
        var concept = new[] { (1L, true, false), (1L, false, true) }; // duplicate QID -> failure
        using var w = new SqliteBuilderWorld(concept,
            new[] { (1L, "en", "label", "a") },
            new[] { (1L, 2L) },
            new[] { (1L, 2L) });
        var r = w.Build();
        Assert.Equal("FAILED", r.Verdict);
        Assert.NotNull(r.StagingDir);
        string state = Path.Combine(r.StagingDir!, "build.state.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(state));
        Assert.Equal("Failed", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("511adb9ebd066f1d4d344b80171902d5", doc.RootElement.GetProperty("corpus_id").GetString());
    }

    [Fact]
    public void ParquetKindMapper_IsExact()
    {
        Assert.Equal("INT64", SqliteCandidatePreflight.KindOfType(typeof(long)));
        Assert.Equal("BOOL", SqliteCandidatePreflight.KindOfType(typeof(bool)));
        Assert.Equal("UTF8", SqliteCandidatePreflight.KindOfType(typeof(string)));
        Assert.Equal("UTF8", SqliteCandidatePreflight.KindOfType(typeof(ReadOnlyMemory<char>)));
        Assert.NotEqual("UTF8", SqliteCandidatePreflight.KindOfType(typeof(ReadOnlyMemory<int>)));
        Assert.NotEqual("UTF8", SqliteCandidatePreflight.KindOfType(typeof(long[])));
    }
}
