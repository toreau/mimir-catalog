using System.Text.Json;
using System.Text.Json.Nodes;
using Mimir.Catalog.Storage.Sqlite;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

public class SqliteHardeningTests
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

    private static JsonObject Root() =>
        JsonNode.Parse(File.ReadAllText(RepoRel(Path.Combine("benchmarks", "candidate-a-sqlite-v1.json"))))!.AsObject();

    private static void Reject(Action<JsonObject> edit)
    {
        Assert.Throws<InvalidDataException>(() =>
        {
            var o = Root();
            edit(o);
            SqliteBaselineConfig.Parse(System.Text.Encoding.UTF8.GetBytes(o.ToJsonString()));
        });
    }

    private static JsonObject Table(JsonObject root, string name)
    {
        foreach (var n in root["tables"]!.AsArray())
        {
            var t = n!.AsObject();
            if (t["name"]?.GetValue<string>() == name) return t;
        }
        throw new InvalidDataException("table not found " + name);
    }

    private static JsonObject Column(JsonObject table, string name)
    {
        foreach (var n in table["columns"]!.AsArray())
        {
            var c = n!.AsObject();
            if (c["name"]?.GetValue<string>() == name) return c;
        }
        throw new InvalidDataException("column not found " + name);
    }

    private static JsonObject Index(JsonObject root, string name)
    {
        foreach (var n in root["indexes"]!.AsArray())
        {
            var i = n!.AsObject();
            if (i["name"]?.GetValue<string>() == name) return i;
        }
        throw new InvalidDataException("index not found " + name);
    }

    [Fact]
    public void ConfigId_IsFrozen76ee16b1()
    {
        var cfg = SqliteBaselineConfig.Parse(File.ReadAllBytes(RepoRel(Path.Combine("benchmarks", "candidate-a-sqlite-v1.json"))));
        Assert.Equal("76ee16b121946175aa17dda7dca6e8387bc95803736692459517f07800e1788a", cfg.ConfigId());
    }

    // ---- exact table/column definitions ----
    [Theory]
    [InlineData("lexical_entry", "Value", "type", "BLOB")]
    [InlineData("lexical_entry", "Value", "collation", "NOCASE")]
    [InlineData("lexical_entry", "LexKind", "name", "Kind")]
    [InlineData("concept", "Qid", "primaryKey", null)]
    public void Reject_TableColumnMutation(string table, string column, string field, string? value)
    {
        Reject(root =>
        {
            var col = Column(Table(root, table), column);
            if (value == null)
            {
                col.Remove(field);
                // also keep a non-null false value visible to parser
                col[field] = JsonValue.Create(false);
            }
            else col[field] = JsonValue.Create(value);
        });
    }

    [Fact]
    public void Reject_ColumnAddedOrRemoved_AndTableRenamed_AndFifthTable()
    {
        Reject(root => Table(root, "lexical_entry")["columns"]!.AsArray().RemoveAt(Table(root, "lexical_entry")["columns"]!.AsArray().Count - 1));
        Reject(root => Table(root, "lexical_entry")["columns"]!.AsArray()
            .Add(JsonNode.Parse("{\"name\":\"Extra\",\"type\":\"TEXT\",\"notNull\":false}")));
        Reject(root => Table(root, "instance_of")["name"] = JsonValue.Create("edge_instance"));
        Reject(root => root["tables"]!.AsArray().Add(JsonNode.Parse(
            "{\"name\":\"fifth\",\"organization\":\"rowid\",\"strict\":true,\"columns\":[{\"name\":\"A\",\"type\":\"INTEGER\",\"notNull\":true}]}")));
    }

    [Fact]
    public void Reject_TableReorder_AndColumnReorder()
    {
        Reject(root =>
        {
            var arr = root["tables"]!.AsArray();
            arr.RemoveAt(0);
            arr.Add(JsonNode.Parse(Table(Root(), "concept").ToJsonString()));
        });
        Reject(root =>
        {
            var cols = Table(root, "lexical_entry")["columns"]!.AsArray();
            var arr = cols.ToList();
            cols.RemoveAt(0);
            cols.Add(arr[0]!.DeepClone());
        });
    }

    // ---- exact index definitions ----
    [Theory]
    [InlineData("lex_lang_value", "Value COLLATE BINARY|Lang COLLATE BINARY")]     // reversed
    [InlineData("inst_subject", "TargetQid|SubjectQid")]                          // reversed
    [InlineData("lex_qid", "Qid|Value COLLATE BINARY|LexKind COLLATE BINARY")]    // Value instead of Lang
    [InlineData("lex_qid", "Qid|Lang COLLATE BINARY")]                            // LexKind missing
    public void Reject_IndexColumnMutation(string index, string columnsJoined)
    {
        Reject(root =>
        {
            var arr = Index(root, index)["columns"]!.AsArray();
            arr.Clear();
            foreach (var c in columnsJoined.Split('|')) arr.Add(JsonValue.Create(c));
        });
    }

    [Fact]
    public void Reject_IndexTableReassignment_ExtraAndMissingIndex()
    {
        Reject(root => Index(root, "lex_lang_value")["table"] = JsonValue.Create("instance_of"));
        Reject(root => root["indexes"]!.AsArray().Add(JsonNode.Parse(
            "{\"name\":\"reverse_target\",\"table\":\"instance_of\",\"columns\":[\"TargetQid\"]}")));
        Reject(root => root["indexes"]!.AsArray().RemoveAt(0));
    }

    // ---- nested unknown fields ----
    [Fact]
    public void Reject_UnknownNestedFields()
    {
        Reject(root => ((JsonObject)root["buildPragmas"]!)["mmap_size"] = JsonValue.Create(0));
        Reject(root => ((JsonObject)root["readPragmas"]!)["page_size"] = JsonValue.Create(4096));
        Reject(root => ((JsonObject)root["readPragmas"]!)["journal_mode"] = JsonValue.Create("OFF"));
        Reject(root => ((JsonObject)root["readPragmas"]!)["cache_size"] = JsonValue.Create(0));
        Reject(root => Table(root, "concept")["bogus"] = JsonValue.Create(1));
        Reject(root => Column(Table(root, "lexical_entry"), "Value")["bogus"] = JsonValue.Create(1));
        Reject(root => Index(root, "sub_subject")["bogus"] = JsonValue.Create(1));
    }

    // ---- exact pragma vocabularies ----
    [Fact]
    public void Reject_MissingRequiredPragma()
    {
        Reject(root => ((JsonObject)root["buildPragmas"]!).Remove("journal_mode"));
        Reject(root => ((JsonObject)root["readPragmas"]!).Remove("automatic_index"));
    }
}

public class SqlitePhysicalParityTests
{
    private static string Normalize(string sql) => new string(sql.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static Microsoft.Data.Sqlite.SqliteConnection Create(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "mimir-parity-" + Guid.NewGuid().ToString("N") + ".db");
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        SqliteCandidateSchema.ApplyBuildPragmas(conn);
        SqliteCandidateSchema.CreateSchema(conn);
        SqliteCandidateSchema.CreateIndexes(conn);
        return conn;
    }

    private static string Scalar(Microsoft.Data.Sqlite.SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (cmd.ExecuteScalar() as string)!;
    }

    private static string RenderTable(SqliteBaselineConfig.TableDef t)
    {
        var cols = new List<string>();
        foreach (var col in t.Columns)
        {
            if (col.PrimaryKey) { cols.Add($"{col.Name} INTEGER PRIMARY KEY"); continue; }
            string s = $"{col.Name} {col.Type}" + (col.NotNull ? " NOT NULL" : "");
            if (col.Collation != null) s += $" COLLATE {col.Collation}";
            cols.Add(s);
        }
        return $"CREATE TABLE {t.Name} ({string.Join(", ", cols)}) STRICT";
    }

    [Fact]
    public void Physical_Tables_MatchFrozenConfigExactly()
    {
        var conn = Create(out string path);
        try
        {
            foreach (var t in SqliteBaselineConfig.DefaultTables())
            {
                string actual = Scalar(conn, $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{t.Name}'");
                Assert.Equal(Normalize(RenderTable(t)), Normalize(actual));
            }
        }
        finally { conn.Dispose(); try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Physical_Indexes_MatchFrozenConfigExactly()
    {
        var conn = Create(out string path);
        try
        {
            foreach (var i in SqliteBaselineConfig.DefaultIndexes())
            {
                string expected = $"CREATE INDEX {i.Name} ON {i.Table} ({string.Join(", ", i.Columns)})";
                string actual = Scalar(conn, $"SELECT sql FROM sqlite_master WHERE type='index' AND name='{i.Name}'");
                Assert.Equal(Normalize(expected), Normalize(actual));
            }
        }
        finally { conn.Dispose(); try { File.Delete(path); } catch { } }
    }
}
