using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mimir.Catalog.Storage.Sqlite;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

public class SqliteFoundationTests
{
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

    private static string TrackedConfigPath() => RepoRel(Path.Combine("benchmarks", "candidate-a-sqlite-v1.json"));

    private static SqliteBaselineConfig ParseTracked()
        => SqliteBaselineConfig.Parse(File.ReadAllBytes(TrackedConfigPath()));

    private static JsonObject Root() => JsonNode.Parse(File.ReadAllText(TrackedConfigPath()))!.AsObject();

    private static void Reject(Action<JsonObject> edit)
    {
        Assert.Throws<InvalidDataException>(() =>
        {
            var o = Root();
            edit(o);
            SqliteBaselineConfig.Parse(System.Text.Encoding.UTF8.GetBytes(o.ToJsonString()));
        });
    }

    // ---- config parsing + identity ----
    [Fact]
    public void Config_TrackedParses_EqualsDefaults()
    {
        var parsed = ParseTracked();
        Assert.Equal(SqliteBaselineConfig.Default().ToCanonicalBytes(), parsed.ToCanonicalBytes());
        Assert.Equal(64, parsed.ConfigId().Length);
    }

    [Fact]
    public void Config_Deterministic()
    {
        Assert.Equal(ParseTracked().ConfigId(), ParseTracked().ConfigId());
    }

    [Fact]
    public void Config_Rejects_UnknownAndMissingFields()
    {
        Reject(o => { o["bogus"] = JsonValue.Create(1); });
        Reject(o => { o.Remove("providerName"); });
        Reject(o => { o["providerName"] = JsonValue.Create("SomeoneElse"); });
        Reject(o => { o["workloadId"] = JsonValue.Create("0".PadRight(64, '0')); });
        Reject(o => ((JsonObject)o["readPragmas"]!)["automatic_index"] = JsonValue.Create("ON"));
        Reject(o => ((JsonObject)o["buildPragmas"]!)["automatic_index"] = JsonValue.Create("ON"));
        Reject(o => ((JsonObject)o["indexes"]!.AsArray()[0]!)["table"] = JsonValue.Create("instance_of"));
    }

    [Fact]
    public void Config_FormattingOrder_DoesNotAlterId()
    {
        var root = Root();
        var reordered = new JsonObject();
        // Deliberately different property order.
        foreach (var key in root.Select(kv => kv.Key).Reverse()) reordered[key] = root[key]!.DeepClone();
        var a = ParseTracked().ConfigId();
        var b = SqliteBaselineConfig.Parse(System.Text.Encoding.UTF8.GetBytes(reordered.ToJsonString())).ConfigId();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Config_NormativeChange_AltersId()
    {
        string baseId = SqliteBaselineConfig.Default().ConfigId();
        var altered = new SqliteBaselineConfig { BuildJournalMode = "DELETE" };
        Assert.NotEqual(baseId, altered.ConfigId());
        Assert.NotEqual(baseId, new SqliteBaselineConfig { ReadAutomaticIndex = "ON" }.ConfigId());
    }

    // ---- schema ----
    private static SqliteConnection OpenDb(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "mimir-a4a-" + Guid.NewGuid().ToString("N") + ".db");
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        SqliteCandidateSchema.ApplyBuildPragmas(conn);
        SqliteCandidateSchema.CreateSchema(conn);
        SqliteCandidateSchema.CreateIndexes(conn);
        return conn;
    }

    private static void Cleanup(SqliteConnection conn, string path)
    {
        conn.Dispose();
        try { File.Delete(path); } catch { /* ignore */ }
        try { File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch { /* ignore */ }
    }

    [Fact]
    public void Schema_CreatesFourStrictTables()
    {
        var conn = OpenDb(out string path);
        try
        {
            foreach (var name in SqliteCandidateSchema.TableNames)
            {
                var sql = Scalar<string>(conn, "SELECT sql FROM sqlite_master WHERE type='table' AND name=@p0", name);
                Assert.NotNull(sql);
                Assert.Contains("STRICT", sql, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void Schema_OnlyConceptHasPrimaryKeySemantics()
    {
        var conn = OpenDb(out string path);
        try
        {
            foreach (var name in SqliteCandidateSchema.TableNames)
            {
                int pkCount = (int)Scalar<long>(conn, "SELECT COUNT(*) FROM pragma_table_info(@p0) WHERE pk<>0", name);
                Assert.Equal(name == "concept" ? 1 : 0, pkCount);
            }
            string conceptSql = Scalar<string>(conn, "SELECT sql FROM sqlite_master WHERE type='table' AND name='concept'")!;
            string edgeSql = Scalar<string>(conn, "SELECT sql FROM sqlite_master WHERE type='table' AND name='instance_of'")!;
            Assert.Contains("PRIMARY KEY", conceptSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PRIMARY KEY", edgeSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UNIQUE", edgeSql, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void Duplicate_LexicalAndEdgeRows_Preserved()
    {
        var conn = OpenDb(out string path);
        try
        {
            Exec(conn, "INSERT INTO lexical_entry (Qid,Lang,LexKind,Value) VALUES (1,'en','label','Alpha'),(1,'en','label','Alpha')");
            Assert.Equal(2L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry"));
            Exec(conn, "INSERT INTO instance_of (SubjectQid,TargetQid) VALUES (5,9),(5,9)");
            Exec(conn, "INSERT INTO subclass_of (SubjectQid,TargetQid) VALUES (5,9),(5,9)");
            Assert.Equal(2L, Scalar<long>(conn, "SELECT COUNT(*) FROM instance_of"));
            Assert.Equal(2L, Scalar<long>(conn, "SELECT COUNT(*) FROM subclass_of"));
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void LexicalEquality_IsCaseSensitive()
    {
        var conn = OpenDb(out string path);
        try
        {
            Exec(conn, "INSERT INTO lexical_entry (Qid,Lang,LexKind,Value) VALUES (1,'en','label','Alpha'),(2,'en','label','alpha')");
            Assert.Equal(1L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry WHERE Lang='en' AND Value='Alpha'"));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry WHERE Lang='en' AND Value='alpha'"));
            Assert.Equal(0L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry WHERE Lang='en' AND Value='ALPHA'"));
            Assert.Equal(0L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry WHERE Lang='EN' AND Value='Alpha'"));
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void LexicalEquality_RawUnicodeByteDistinct()
    {
        var conn = OpenDb(out string path);
        try
        {
            Exec(conn, "INSERT INTO lexical_entry (Qid,Lang,LexKind,Value) VALUES (1,'nb','label',@p0),(2,'nb','label',@p1)",
                "\u00e9", "e\u0301"); // é vs e + combining acute
            Assert.Equal(1L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry WHERE Lang='nb' AND Value=@p0", "\u00e9"));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry WHERE Lang='nb' AND Value=@p0", "e\u0301"));
            Assert.Equal(2L, Scalar<long>(conn, "SELECT COUNT(*) FROM lexical_entry"));
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void Schema_NoNocase_NoNormalizedIndex()
    {
        var conn = OpenDb(out string path);
        try
        {
            var parts = new List<string>();
            foreach (var name in SqliteCandidateSchema.TableNames.Concat(SqliteCandidateSchema.Indexes.Select(i => i.Name)))
                parts.Add(Scalar<string>(conn, "SELECT sql FROM sqlite_master WHERE name=@p0", name)!);
            string sql = string.Join("\n", parts);
            Assert.DoesNotContain("NOCASE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Lookup", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("norm", sql, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void Indexes_ExactFourSecondary_NoReverseEdgeIndex()
    {
        var conn = OpenDb(out string path);
        try
        {
            string[] names = IndexNames(conn);
            Assert.Equal(new[] { "inst_subject", "lex_lang_value", "lex_qid", "sub_subject" }, names);
            foreach (var idx in new[] { "inst_subject", "sub_subject" })
            {
                string sql = Scalar<string>(conn, "SELECT sql FROM sqlite_master WHERE type='index' AND name=@p0", idx)!;
                Assert.StartsWith("CREATE INDEX", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("UNIQUE", sql, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("SubjectQid", sql);
            }
            Assert.DoesNotContain("_target", string.Join(",", names), StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void Connections_AutomaticIndexAndForeignKeysExplicitOff()
    {
        var conn = OpenDb(out string path);
        try
        {
            SqliteCandidateSchema.ApplyReadSettings(conn);
            Assert.Equal(0L, Scalar<long>(conn, "PRAGMA automatic_index"));
            Assert.Equal(0L, Scalar<long>(conn, "PRAGMA foreign_keys"));
            Assert.Equal(0L, Scalar<long>(conn, "PRAGMA synchronous"));
        }
        finally { Cleanup(conn, path); }
    }

    [Fact]
    public void Versions_Reported()
    {
        var (provider, lib) = SqliteCandidateSchema.RuntimeVersions();
        Assert.Contains("Microsoft.Data.Sqlite", provider);
        Assert.False(string.IsNullOrWhiteSpace(lib));
        Assert.StartsWith("3.", lib);
    }

    // helpers
    private static T Scalar<T>(SqliteConnection c, string sql, params object[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@p" + i, args[i]);
        object? v = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(v ?? 0, typeof(T));
    }

    private static void Exec(SqliteConnection c, string sql, params object[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@p" + i, args[i]);
        cmd.ExecuteNonQuery();
    }

    private static string[] IndexNames(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list.ToArray();
    }
}
