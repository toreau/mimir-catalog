using Microsoft.Data.Sqlite;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Minimal Candidate A analytical scan adapter: four lazy full-relation scans
/// over a read-only Candidate A database. No digest/correctness logic, no
/// DISTINCT/ORDER BY/GROUP BY; duplicate multiplicity is preserved. Lifecycle
/// (Open/Dispose) is caller-owned.
/// </summary>
public sealed class SqliteAnalyticalCandidate : IAnalyticalCandidate
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteAnalyticalCandidate(string dbPath) => _dbPath = dbPath;

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(_dbPath)) throw new FileNotFoundException("candidate database does not exist", _dbPath);
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        SqliteCandidateSchema.ApplyReadSettings(conn);
        _connection = conn;
    }

    private SqliteConnection Require()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection ?? throw new InvalidOperationException("candidate not open");
    }

    public IEnumerable<ConceptRow> ScanConcept()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT Qid, InT1, InT2 FROM concept";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int in1 = reader.GetInt32(1);
            int in2 = reader.GetInt32(2);
            if (in1 is not (0 or 1) || in2 is not (0 or 1))
                throw new InvalidDataException($"invalid stored Concept boolean: InT1={in1} InT2={in2}");
            yield return new ConceptRow(reader.GetInt64(0), in1 == 1, in2 == 1);
        }
    }

    public IEnumerable<LexicalRow> ScanLexicalEntry()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT Qid, Lang, LexKind, Value FROM lexical_entry";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return new LexicalRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public IEnumerable<EdgeRow> ScanInstanceOf()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT SubjectQid, TargetQid FROM instance_of";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return new EdgeRow(reader.GetInt64(0), reader.GetInt64(1));
    }

    public IEnumerable<EdgeRow> ScanSubclassOf()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT SubjectQid, TargetQid FROM subclass_of";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return new EdgeRow(reader.GetInt64(0), reader.GetInt64(1));
    }

    public IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT Lang, LexKind, COUNT(*) FROM lexical_entry GROUP BY Lang, LexKind";
        var list = new List<(string, string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return list;
    }

    public IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT TargetQid, COUNT(*) FROM instance_of GROUP BY TargetQid";
        return ReadTargetCounts(cmd);
    }

    public IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout()
    {
        using var cmd = Require().CreateCommand();
        cmd.CommandText = "SELECT TargetQid, COUNT(*) FROM subclass_of GROUP BY TargetQid";
        return ReadTargetCounts(cmd);
    }

    private static IReadOnlyList<(long TargetQid, long Count)> ReadTargetCounts(SqliteCommand cmd)
    {
        var list = new List<(long, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return list;
    }

    public IReadOnlyList<A5Row> A5P31TargetLabels() => throw new NotSupportedException("A5 belongs to a later slice");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
    }
}
