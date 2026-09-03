using Microsoft.Data.Sqlite;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Frozen Candidate A schema, build PRAGMAs, read settings and index creation.
/// Mirrors benchmarks/candidate-a-sqlite-v1.json. Read-only helper API for the
/// later builder slice; never ingests corpus data.
/// </summary>
public static class SqliteCandidateSchema
{
    public static readonly string[] TableNames = ["concept", "lexical_entry", "instance_of", "subclass_of"];

    public static readonly (string Name, string Table, string[] Columns)[] Indexes =
    [
        ("lex_lang_value", "lexical_entry", ["Lang COLLATE BINARY", "Value COLLATE BINARY"]),
        ("lex_qid", "lexical_entry", ["Qid", "Lang COLLATE BINARY", "LexKind COLLATE BINARY"]),
        ("inst_subject", "instance_of", ["SubjectQid", "TargetQid"]),
        ("sub_subject", "subclass_of", ["SubjectQid", "TargetQid"]),
    ];

    public static void ApplyBuildPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA page_size = 4096;
            PRAGMA synchronous = OFF;
            PRAGMA foreign_keys = OFF;
            PRAGMA automatic_index = OFF;
            """;
        cmd.ExecuteNonQuery();
        // journal_mode is a separate statement because it may return a row and
        // must run outside any open transaction.
        using var jm = conn.CreateCommand();
        jm.CommandText = "PRAGMA journal_mode = OFF;";
        jm.ExecuteNonQuery();
    }

    /// <summary>Read/benchmark connection settings (explicit, not SQLite defaults).</summary>
    public static void ApplyReadSettings(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = OFF; PRAGMA automatic_index = OFF;";
        cmd.ExecuteNonQuery();
    }

    public static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE concept (
              Qid INTEGER PRIMARY KEY,
              InT1 INTEGER NOT NULL,
              InT2 INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE lexical_entry (
              Qid INTEGER NOT NULL,
              Lang TEXT NOT NULL COLLATE BINARY,
              LexKind TEXT NOT NULL COLLATE BINARY,
              Value TEXT NOT NULL COLLATE BINARY
            ) STRICT;

            CREATE TABLE instance_of (
              SubjectQid INTEGER NOT NULL,
              TargetQid INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE subclass_of (
              SubjectQid INTEGER NOT NULL,
              TargetQid INTEGER NOT NULL
            ) STRICT;
            """;
        cmd.ExecuteNonQuery();
    }

    public static void CreateIndexes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX lex_lang_value ON lexical_entry (Lang COLLATE BINARY, Value COLLATE BINARY);
            CREATE INDEX lex_qid ON lexical_entry (Qid, Lang COLLATE BINARY, LexKind COLLATE BINARY);
            CREATE INDEX inst_subject ON instance_of (SubjectQid, TargetQid);
            CREATE INDEX sub_subject ON subclass_of (SubjectQid, TargetQid);
            """;
        cmd.ExecuteNonQuery();
    }

    public static (string Provider, string SqliteLib) RuntimeVersions()
    {
        // Force SQLitePCLRaw bundle initialization (idempotent through
        // Microsoft.Data.Sqlite's connection provider) before reading libversion.
        using (var init = new SqliteConnection("Data Source=:memory:"))
        {
            init.Open();
        }
        var asm = typeof(SqliteConnection).Assembly.GetName();
        string provider = $"{asm.Name} {asm.Version}";
        string lib = SQLitePCL.raw.sqlite3_libversion().utf8_to_string();
        return (provider, lib);
    }
}
