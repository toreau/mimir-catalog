using Microsoft.Data.Sqlite;

namespace Mimir.Catalog.Corpus;

/// <summary>
/// Temporary disk-backed aggregator for Pass A. SQLite is used purely as an
/// implementation detail for exact, bounded-memory aggregation of very
/// high-cardinality edge fanout. Its use here is not a production storage
/// decision. No DuckDB fallback exists in this slice by design.
/// </summary>
public sealed class SqliteSink : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insertP31;
    private readonly SqliteCommand _insertP279;
    private readonly SqliteCommand _insertPresence;
    private bool _transactionOpen;

    public SqliteSink(string dbPath)
    {
        DbPath = dbPath;
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private }.ToString();
        _connection = new SqliteConnection(cs);
        _connection.Open();

        Exec("PRAGMA journal_mode=OFF");
        Exec("PRAGMA synchronous=OFF");
        Exec("PRAGMA temp_store=FILE");
        Exec("PRAGMA cache_size=-262144");

        Exec("CREATE TABLE IF NOT EXISTS p31(s INTEGER NOT NULL, o INTEGER NOT NULL)");
        Exec("CREATE TABLE IF NOT EXISTS p279(s INTEGER NOT NULL, o INTEGER NOT NULL)");
        Exec("CREATE TABLE IF NOT EXISTS presence(qid INTEGER NOT NULL, flags INTEGER NOT NULL)");

        _insertP31 = _connection.CreateCommand();
        _insertP31.CommandText = "INSERT INTO p31(s,o) VALUES ($s,$o)";
        _insertP31.Parameters.Add("$s", SqliteType.Integer);
        _insertP31.Parameters.Add("$o", SqliteType.Integer);
        _insertP31.Prepare();

        _insertP279 = _connection.CreateCommand();
        _insertP279.CommandText = "INSERT INTO p279(s,o) VALUES ($s,$o)";
        _insertP279.Parameters.Add("$s", SqliteType.Integer);
        _insertP279.Parameters.Add("$o", SqliteType.Integer);
        _insertP279.Prepare();

        _insertPresence = _connection.CreateCommand();
        _insertPresence.CommandText = "INSERT INTO presence(qid,flags) VALUES ($q,$f)";
        _insertPresence.Parameters.Add("$q", SqliteType.Integer);
        _insertPresence.Parameters.Add("$f", SqliteType.Integer);
        _insertPresence.Prepare();
    }

    public string DbPath { get; }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Begin()
    {
        if (_transactionOpen) return;
        Exec("BEGIN IMMEDIATE");
        _transactionOpen = true;
    }

    public void Commit()
    {
        if (!_transactionOpen) return;
        Exec("COMMIT");
        _transactionOpen = false;
    }

    public void AddP31(long s, long o)
    {
        _insertP31.Parameters[0].Value = s;
        _insertP31.Parameters[1].Value = o;
        _insertP31.ExecuteNonQuery();
    }

    public void AddP279(long s, long o)
    {
        _insertP279.Parameters[0].Value = s;
        _insertP279.Parameters[1].Value = o;
        _insertP279.ExecuteNonQuery();
    }

    /// <summary>flags bit 0 = en label present, bit 1 = nb label present.</summary>
    public void AddPresence(long qid, int flags)
    {
        _insertPresence.Parameters[0].Value = qid;
        _insertPresence.Parameters[1].Value = flags;
        _insertPresence.ExecuteNonQuery();
    }

    public long QueryLong(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? 0 : Convert.ToInt64(r);
    }

    public SqliteCommand CreateCommand(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_transactionOpen)
        {
            try { Exec("ROLLBACK"); } catch { /* temp artifact */ }
        }
        _insertP31.Dispose();
        _insertP279.Dispose();
        _insertPresence.Dispose();
        _connection.Dispose();
    }
}
