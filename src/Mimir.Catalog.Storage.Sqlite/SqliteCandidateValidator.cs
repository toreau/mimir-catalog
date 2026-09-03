using Microsoft.Data.Sqlite;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Post-build validation of a Candidate A database: frozen physical baseline
/// checks plus A1 MultisetFoldV1 correctness scanned from SQLite rows. Reads
/// logical rows; never trusts metadata-only counts.
/// </summary>
public static class SqliteCandidateValidator
{
    public sealed class Outcome
    {
        public bool Ok { get; set; }
        public List<string> Reasons { get; } = new();
        public Dictionary<string, (long Cardinality, string Digest)> A1Actual { get; } = new();
    }

    private static string Squash(string sql) => new string(sql.Where(c => !char.IsWhiteSpace(c)).ToArray());

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

    public static Outcome Validate(string dbPath, SqliteBaselineConfig config,
        IReadOnlyDictionary<string, (long Cardinality, string Digest)> a1Expected)
    {
        var o = new Outcome();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        SqliteCandidateSchema.ApplyReadSettings(conn);

        // Physical tables: exact set + DDL parity with the frozen config.
        var tables = Query(conn, "SELECT name, sql FROM sqlite_master WHERE type='table'")
            .ToDictionary(r => r.Item1, r => r.Item2);
        var want = config.Tables;
        if (tables.Count != want.Count) o.Reasons.Add($"table count {tables.Count} != {want.Count}");
        foreach (var t in want)
        {
            if (!tables.TryGetValue(t.Name, out var sql))
            {
                o.Reasons.Add($"missing table {t.Name}");
                continue;
            }
            if (Squash(sql) != Squash(RenderTable(t))) o.Reasons.Add($"table DDL parity mismatch: {t.Name}");
        }

        // Physical indexes: exact four reviewed indexes, nothing else.
        var idx = Query(conn, "SELECT name, sql FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'")
            .ToDictionary(r => r.Item1, r => r.Item2);
        var wantIdx = config.Indexes;
        if (idx.Count != wantIdx.Count) o.Reasons.Add($"index count {idx.Count} != {wantIdx.Count}");
        foreach (var i in wantIdx)
        {
            if (!idx.TryGetValue(i.Name, out var sql))
            {
                o.Reasons.Add($"missing index {i.Name}");
                continue;
            }
            string expected = $"CREATE INDEX {i.Name} ON {i.Table} ({string.Join(", ", i.Columns)})";
            if (Squash(sql) != Squash(expected)) o.Reasons.Add($"index DDL parity mismatch: {i.Name}");
        }
        if (idx.Keys.Any(n => n.Contains("target", StringComparison.OrdinalIgnoreCase)))
            o.Reasons.Add("unexpected reverse edge index present");

        // Connection settings + page size.
        if (ScalarLong(conn, "PRAGMA page_size") != 4096) o.Reasons.Add("page_size != 4096");
        if (ScalarLong(conn, "PRAGMA automatic_index") != 0) o.Reasons.Add("automatic_index != OFF");
        if (ScalarLong(conn, "PRAGMA foreign_keys") != 0) o.Reasons.Add("foreign_keys != OFF");

        // A1 fold over logical rows scanned from SQLite.
        var concept = a1Expected["A1-Concept"];
        o.A1Actual["A1-Concept"] = Fold(conn, "concept", (reader, fold) =>
            fold.Add(MultisetFoldV1.ConceptRow(reader.GetInt64(0), reader.GetInt32(1) != 0, reader.GetInt32(2) != 0)));
        var lexical = a1Expected["A1-LexicalEntry"];
        o.A1Actual["A1-LexicalEntry"] = Fold(conn, "lexical_entry", (reader, fold) =>
            fold.Add(MultisetFoldV1.LexicalRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))));
        o.A1Actual["A1-InstanceOf"] = Fold(conn, "instance_of", (reader, fold) =>
            fold.Add(MultisetFoldV1.EdgeRow(reader.GetInt64(0), reader.GetInt64(1))));
        o.A1Actual["A1-SubclassOf"] = Fold(conn, "subclass_of", (reader, fold) =>
            fold.Add(MultisetFoldV1.EdgeRow(reader.GetInt64(0), reader.GetInt64(1))));

        foreach (var op in new[] { "A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf" })
        {
            var (expCard, expDigest) = a1Expected[op];
            var (actCard, actDigest) = o.A1Actual[op];
            if (expCard != actCard) o.Reasons.Add($"{op}: cardinality {actCard} != expected {expCard}");
            if (expDigest != actDigest) o.Reasons.Add($"{op}: digest mismatch");
        }

        o.Ok = o.Reasons.Count == 0;
        return o;
    }

    private static (long Cardinality, string Digest) Fold(SqliteConnection conn, string table,
        Action<SqliteDataReader, MultisetFoldV1> add)
    {
        var fold = new MultisetFoldV1();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) add(reader, fold);
        return (fold.Count, fold.Digest());
    }

    private static List<(string, string)> Query(SqliteConnection conn, string sql)
    {
        var list = new List<(string, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
