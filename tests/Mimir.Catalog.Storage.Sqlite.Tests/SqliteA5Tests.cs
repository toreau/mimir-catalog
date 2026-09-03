using Microsoft.Data.Sqlite;
using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

public class SqliteA5Tests : IDisposable
{
    private readonly string _db;
    private readonly SqliteConnection _conn;

    public SqliteA5Tests()
    {
        _db = Path.Combine(Path.GetTempPath(), "mimir-a5-" + Guid.NewGuid().ToString("N") + ".db");
        _conn = new SqliteConnection($"Data Source={_db}");
        _conn.Open();
        SqliteCandidateSchema.ApplyBuildPragmas(_conn);
        SqliteCandidateSchema.CreateSchema(_conn);
        SqliteCandidateSchema.CreateIndexes(_conn);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO concept (Qid,InT1,InT2) VALUES (5,0,1),(6,0,1),(7,0,1),(8,0,1),(9,0,1);
            INSERT INTO instance_of (SubjectQid,TargetQid) VALUES (1,5),(2,5),(1,6),(1,7),(1,8),(1,9);
            INSERT INTO lexical_entry (Qid,Lang,LexKind,Value) VALUES
              (5,'en','label','L5'),(5,'en','label','L5'),  -- identical duplicate label rows
              (5,'nb','label','N5'),
              (5,'en','alias','IGNORED'),
              (6,'en','label','E6'),
              (7,'nb','label','N7'),
              (9,'en','label','');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { File.Delete(_db); } catch { }
    }

    [Fact]
    public void A5_JoinShape_NoFanoutMultiplication_AndLabels()
    {
        using var a = new SqliteAnalyticalCandidate(_db);
        a.Open();
        var rows = a.A5P31TargetLabels();
        Assert.Equal(5, rows.Count);
        var map = rows.ToDictionary(r => r.TargetQid);

        Assert.Equal(2L, map[5].Fanout);            // duplicate instance rows counted, lexical duplicates do NOT multiply
        Assert.Equal("L5", map[5].EnLabel);         // alias excluded
        Assert.Equal("N5", map[5].NbLabel);

        Assert.Equal(1L, map[6].Fanout);
        Assert.Equal("E6", map[6].EnLabel);         // en-only
        Assert.Null(map[6].NbLabel);

        Assert.Null(map[7].EnLabel);                // nb-only
        Assert.Equal("N7", map[7].NbLabel);

        Assert.Null(map[8].EnLabel);                // unlabeled preserved
        Assert.Null(map[8].NbLabel);

        Assert.Equal("", map[9].EnLabel);           // empty string distinct from null
        Assert.Null(map[9].NbLabel);
    }

    [Fact]
    public void A5_Runner_OverSqlite_Valid()
    {
        using var a = new SqliteAnalyticalCandidate(_db);
        a.Open();
        var rows = a.A5P31TargetLabels();
        var sorted = rows.OrderBy(r => r.TargetQid)
            .Select(r => WorkloadOracle.A5Row(r.TargetQid, r.Fanout, r.EnLabel, r.NbLabel)).ToArray();
        var workload = new AnalyticalWorkload
        {
            Expected = new Dictionary<string, A1Expected>(StringComparer.Ordinal)
            {
                ["A5"] = new("A5", rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted)),
            },
        };
        var result = new A5CorrectnessRunner(a).Run(workload);
        Assert.Equal(ServingStatuses.Valid, result.Status);
    }
}
