using Microsoft.Data.Sqlite;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Candidate A runtime storage primitive over a published SQLite database.
/// Read-only, frozen read settings, fully materialized results, no workload
/// canonicalization, no memoization. Prepared commands are created once per
/// Open and reused.
/// </summary>
public sealed class SqliteStorageCandidate : IStorageCandidate
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private SqliteCommand? _s1;
    private SqliteCommand? _s2;
    private SqliteCommand? _s3;
    private SqliteCommand? _s4;
    private SqliteCommand? _s5;
    private SqliteParameter _s1Qid = null!;
    private SqliteParameter _s2Lang = null!;
    private SqliteParameter _s2Value = null!;
    private SqliteParameter _s3Qid = null!;
    private SqliteParameter _s4Qid = null!;
    private SqliteParameter _s5Qid = null!;
    private bool _disposed;

    public SqliteStorageCandidate(string dbPath) => _dbPath = dbPath;

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(_dbPath)) throw new FileNotFoundException("candidate database does not exist", _dbPath);
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        SqliteCandidateSchema.ApplyReadSettings(conn);

        _s1 = conn.CreateCommand();
        _s1.CommandText = "SELECT InT1, InT2 FROM concept WHERE Qid = @qid";
        _s1Qid = _s1.Parameters.Add("@qid", SqliteType.Integer);
        _s1.Prepare();

        _s2 = conn.CreateCommand();
        _s2.CommandText = "SELECT Qid, LexKind FROM lexical_entry WHERE Lang = @lang AND Value = @value";
        _s2Lang = _s2.Parameters.Add("@lang", SqliteType.Text);
        _s2Value = _s2.Parameters.Add("@value", SqliteType.Text);
        _s2.Prepare();

        _s3 = conn.CreateCommand();
        _s3.CommandText = "SELECT Qid, Lang, LexKind, Value FROM lexical_entry WHERE Qid = @qid";
        _s3Qid = _s3.Parameters.Add("@qid", SqliteType.Integer);
        _s3.Prepare();

        _s4 = conn.CreateCommand();
        _s4.CommandText = "SELECT TargetQid FROM instance_of WHERE SubjectQid = @qid";
        _s4Qid = _s4.Parameters.Add("@qid", SqliteType.Integer);
        _s4.Prepare();

        _s5 = conn.CreateCommand();
        _s5.CommandText = "SELECT TargetQid FROM subclass_of WHERE SubjectQid = @qid";
        _s5Qid = _s5.Parameters.Add("@qid", SqliteType.Integer);
        _s5.Prepare();

        _connection = conn;
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection == null) throw new InvalidOperationException("candidate not open");
    }

    public ConceptHit GetConcept(long qid)
    {
        EnsureOpen();
        _s1Qid.Value = qid;
        using var reader = _s1!.ExecuteReader();
        if (!reader.Read()) return new ConceptHit(false, false, false);
        int in1 = reader.GetInt32(0);
        int in2 = reader.GetInt32(1);
        if (in1 is not (0 or 1) || in2 is not (0 or 1))
            throw new InvalidDataException($"invalid stored Concept boolean: InT1={in1} InT2={in2}");
        return new ConceptHit(true, in1 == 1, in2 == 1);
    }

    public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value)
    {
        EnsureOpen();
        _s2Lang.Value = lang;
        _s2Value.Value = value;
        var result = new List<LexicalHit>();
        using var reader = _s2!.ExecuteReader();
        while (reader.Read()) result.Add(new LexicalHit(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid)
    {
        EnsureOpen();
        _s3Qid.Value = qid;
        var result = new List<LexicalRow>();
        using var reader = _s3!.ExecuteReader();
        while (reader.Read())
            result.Add(new LexicalRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public IReadOnlyList<long> GetInstanceOf(long subjectQid)
    {
        EnsureOpen();
        _s4Qid.Value = subjectQid;
        var result = new List<long>();
        using var reader = _s4!.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    public IReadOnlyList<long> GetSubclassOf(long subjectQid)
    {
        EnsureOpen();
        _s5Qid.Value = subjectQid;
        var result = new List<long>();
        using var reader = _s5!.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _s1?.Dispose();
        _s2?.Dispose();
        _s3?.Dispose();
        _s4?.Dispose();
        _s5?.Dispose();
        _connection?.Dispose();
    }
}
