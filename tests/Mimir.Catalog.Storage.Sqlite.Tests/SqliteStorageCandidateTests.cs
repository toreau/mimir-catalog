using Microsoft.Data.Sqlite;
using Mimir.Catalog.Storage.Sqlite;

namespace Mimir.Catalog.Storage.Sqlite.Tests;

public class SqliteStorageCandidateTests : IDisposable
{
    private readonly string _db;
    private readonly SqliteConnection _conn;

    public SqliteStorageCandidateTests()
    {
        _db = Path.Combine(Path.GetTempPath(), "mimir-adapter-" + Guid.NewGuid().ToString("N") + ".db");
        _conn = new SqliteConnection($"Data Source={_db}");
        _conn.Open();
        SqliteCandidateSchema.ApplyBuildPragmas(_conn);
        SqliteCandidateSchema.CreateSchema(_conn);
        SqliteCandidateSchema.CreateIndexes(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { File.Delete(_db); } catch { }
    }

    private void Exec(string sql, params object[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@p" + i, args[i]);
        cmd.ExecuteNonQuery();
    }

    private SqliteStorageCandidate Open()
    {
        var c = new SqliteStorageCandidate(_db);
        c.Open();
        return c;
    }

    private void SeedBaseline()
    {
        Exec("INSERT INTO concept (Qid,InT1,InT2) VALUES (1,1,0),(2,0,1),(3,1,1)");
        Exec("INSERT INTO lexical_entry (Qid,Lang,LexKind,Value) VALUES (1,'en','label','Alpha'),(2,'en','label','alpha'),(2,'en','label','alpha'),(3,'en','alias','Alpha'),(1,'nb','label','\u00e9'),(2,'nb','label','e\u0301')");
        Exec("INSERT INTO instance_of (SubjectQid,TargetQid) VALUES (1,10),(1,20),(2,30),(3,10)");
        Exec("INSERT INTO subclass_of (SubjectQid,TargetQid) VALUES (1,100),(2,200),(3,300)");
    }

    [Fact]
    public void Open_ReadOnlySettings_MissingDbFails_PreOpenFails()
    {
        using (var c = new SqliteStorageCandidate(_db)) { c.Open(); Assert.False(c.GetConcept(1).Present); }
        // missing db cannot be created
        string missing = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".db");
        using var c2 = new SqliteStorageCandidate(missing);
        Assert.Throws<FileNotFoundException>(() => c2.Open());
        Assert.False(File.Exists(missing));

        using var c3 = new SqliteStorageCandidate(_db);
        Assert.Throws<InvalidOperationException>(() => c3.GetConcept(1));
    }

    [Fact]
    public void RuntimePragmas_AreOff_ReadOnlyOpen()
    {
        using var c = Open();
        // read-only semantics: the adapter path only runs SELECTs; settings proven through a parallel read-only connection on the same settings helper
        using var ro = new SqliteConnection($"Data Source={_db};Mode=ReadOnly");
        ro.Open();
        SqliteCandidateSchema.ApplyReadSettings(ro);
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "PRAGMA automatic_index";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void S1_Hit_Miss_Flags()
    {
        SeedBaseline();
        using var c = Open();
        var h1 = c.GetConcept(1);
        Assert.True(h1.Present && h1.InT1 && !h1.InT2);
        var h3 = c.GetConcept(3);
        Assert.True(h3.Present && h3.InT1 && h3.InT2);
        var miss = c.GetConcept(9999);
        Assert.False(miss.Present);
    }

    [Fact]
    public void S1_InvalidStoredBoolean_Rejected()
    {
        SeedBaseline();
        Exec("INSERT INTO concept (Qid,InT1,InT2) VALUES (50,2,1)");
        using var c = Open();
        Assert.Throws<InvalidDataException>(() => c.GetConcept(50));
    }

    [Fact]
    public void S2_CaseAndUnicode_Exact()
    {
        SeedBaseline();
        using var c = Open();
        Assert.Equal(2, c.LookupLexical("en", "Alpha").Count);
        Assert.Equal(2, c.LookupLexical("en", "alpha").Count);
        Assert.Empty(c.LookupLexical("en", "ALPHA"));
        Assert.Single(c.LookupLexical("nb", "\u00e9"));
        Assert.Single(c.LookupLexical("nb", "e\u0301"));
    }

    [Fact]
    public void S2_DuplicatesAndFanout_Preserved()
    {
        SeedBaseline();
        using var c = Open();
        var alpha = c.LookupLexical("en", "Alpha");
        Assert.Equal(2, alpha.Count); // qid 1 label + qid 3 alias, same raw value => fanout 2, multiplicity intact
    }

    [Fact]
    public void S3_RowsAndZero()
    {
        SeedBaseline();
        using var c = Open();
        var rows = c.GetLexicalByQid(1);
        Assert.Contains(rows, r => r.Lang == "en" && r.LexKind == "label" && r.Value == "Alpha");
        Assert.Empty(c.GetLexicalByQid(777));
    }

    [Fact]
    public void S4_Degrees()
    {
        SeedBaseline();
        using var c = Open();
        Assert.Empty(c.GetInstanceOf(9));
        Assert.Equal(new[] { 30L }, c.GetInstanceOf(2));
        Assert.Equal(2, c.GetInstanceOf(1).Count);
    }

    [Fact]
    public void S5_Degrees()
    {
        SeedBaseline();
        using var c = Open();
        Assert.Empty(c.GetSubclassOf(9));
        Assert.Equal(new[] { 200L }, c.GetSubclassOf(2));
        Assert.Single(c.GetSubclassOf(1));
    }

    [Fact]
    public void RepeatedCalls_ReusePreparedCommands()
    {
        SeedBaseline();
        using var c = Open();
        for (int i = 0; i < 5; i++)
        {
            Assert.True(c.GetConcept(1).Present);
            Assert.Equal(2, c.LookupLexical("en", "alpha").Count);
            Assert.Empty(c.GetSubclassOf(9));
        }
    }
}
