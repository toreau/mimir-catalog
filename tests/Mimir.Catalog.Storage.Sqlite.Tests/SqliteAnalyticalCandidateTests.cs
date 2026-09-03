using Microsoft.Data.Sqlite;
using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

public class SqliteAnalyticalCandidateTests : IDisposable
{
    private readonly string _db;
    private readonly SqliteConnection _conn;

    public SqliteAnalyticalCandidateTests()
    {
        _db = Path.Combine(Path.GetTempPath(), "mimir-a1-" + Guid.NewGuid().ToString("N") + ".db");
        _conn = new SqliteConnection($"Data Source={_db}");
        _conn.Open();
        SqliteCandidateSchema.ApplyBuildPragmas(_conn);
        SqliteCandidateSchema.CreateSchema(_conn);
        SqliteCandidateSchema.CreateIndexes(_conn);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO concept (Qid,InT1,InT2) VALUES (1,1,0),(2,0,1);
            INSERT INTO lexical_entry (Qid,Lang,LexKind,Value) VALUES (1,'en','label','Alpha'),(2,'en','label','Alpha');
            INSERT INTO instance_of (SubjectQid,TargetQid) VALUES (1,5),(1,5);
            INSERT INTO subclass_of (SubjectQid,TargetQid) VALUES (1,10),(2,20);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { File.Delete(_db); } catch { }
    }

    [Fact]
    public void Scans_PreserveMultiplicity_AndMapLogicalFields()
    {
        using var a = new SqliteAnalyticalCandidate(_db);
        a.Open();
        Assert.Equal(2, a.ScanConcept().Count());
        Assert.Equal(2, a.ScanLexicalEntry().Count());
        Assert.Equal(2, a.ScanInstanceOf().Count());
        Assert.Equal(2, a.ScanSubclassOf().Count());
        Assert.All(a.ScanLexicalEntry(), r => Assert.Equal("Alpha", r.Value));
        Assert.All(a.ScanInstanceOf(), e => Assert.Equal(5L, e.TargetQid));
    }

    [Fact]
    public void ReadOnlyOpen_MissingDbFails_PreOpenFails()
    {
        using var missing = new SqliteAnalyticalCandidate(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".db"));
        Assert.Throws<FileNotFoundException>(() => missing.Open());

        using var notOpen = new SqliteAnalyticalCandidate(_db);
        Assert.Throws<InvalidOperationException>(() => notOpen.ScanConcept().ToList());
    }

    [Fact]
    public void Runner_OverSqlite_Valid()
    {
        using var a = new SqliteAnalyticalCandidate(_db);
        a.Open();
        // Neutral reference expectations from the same rows via MultisetFoldV1.
        var expected = new Dictionary<string, A1Expected>(StringComparer.Ordinal);
        var fold = new MultisetFoldV1();
        foreach (var r in a.ScanConcept()) fold.Add(MultisetFoldV1.ConceptRow(r.Qid, r.InT1, r.InT2));
        expected["A1-Concept"] = new("A1-Concept", fold.Count, fold.Digest());
        var foldL = new MultisetFoldV1();
        foreach (var r in a.ScanLexicalEntry()) foldL.Add(MultisetFoldV1.LexicalRow(r.Qid, r.Lang, r.LexKind, r.Value));
        expected["A1-LexicalEntry"] = new("A1-LexicalEntry", foldL.Count, foldL.Digest());
        var foldI = new MultisetFoldV1();
        foreach (var r in a.ScanInstanceOf()) foldI.Add(MultisetFoldV1.EdgeRow(r.SubjectQid, r.TargetQid));
        expected["A1-InstanceOf"] = new("A1-InstanceOf", foldI.Count, foldI.Digest());
        var foldS = new MultisetFoldV1();
        foreach (var r in a.ScanSubclassOf()) foldS.Add(MultisetFoldV1.EdgeRow(r.SubjectQid, r.TargetQid));
        expected["A1-SubclassOf"] = new("A1-SubclassOf", foldS.Count, foldS.Digest());

        var results = new A1CorrectnessRunner(a).RunAll(new AnalyticalWorkload { Expected = expected });
        Assert.All(results, r => Assert.Equal(ServingStatuses.Valid, r.Status));
    }
}
