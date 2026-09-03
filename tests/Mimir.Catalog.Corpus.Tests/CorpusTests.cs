using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Mimir.Catalog.Corpus;
using Xunit;

namespace Mimir.Catalog.Corpus.Tests;

public class CorpusCoreTests
{
    [Theory]
    [InlineData("Q1", 1L)]
    [InlineData("Q31", 31L)]
    [InlineData("Q121439429", 121439429L)]
    [InlineData("Q999999999999", 999999999999L)]
    public void Qid_Parses_Valid(string s, long expected)
    {
        Assert.True(Qid.TryParse(s, out long v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("Q0")]
    [InlineData("Q")]
    [InlineData("q31")]
    [InlineData("Q031")]
    [InlineData("Q31a")]
    [InlineData("P31")]
    [InlineData("")]
    [InlineData("Q1 2")]
    public void Qid_Rejects_Invalid(string s)
    {
        Assert.False(Qid.TryParse(s, out _));
        Assert.False(Qid.IsValidItemId(s));
    }

    [Fact]
    public void PropertyId_Parses_Valid()
    {
        Assert.True(Qid.IsValidPropertyId("P31"));
        Assert.True(Qid.IsValidPropertyId("P1234567"));
        Assert.False(Qid.IsValidPropertyId("Q31"));
        Assert.False(Qid.IsValidPropertyId("P031"));
        Assert.False(Qid.IsValidPropertyId("P"));
    }

    [Theory]
    [InlineData(1L, 469L)]
    [InlineData(5L, 729L)]
    [InlineData(31L, 260L)]
    [InlineData(9143L, 525L)]
    [InlineData(123456L, 144L)]
    [InlineData(5233394L, 110L)]
    public void T1_Golden_NonMembers(long qid, long bucket)
    {
        Assert.Equal(bucket, CorpusHash.Bucket(qid));
        Assert.False(CorpusHash.IsT1(qid));
    }

    [Theory]
    [InlineData(107L, 14L)]
    [InlineData(164L, 5L)]
    [InlineData(190L, 8L)]
    [InlineData(197L, 3L)]
    [InlineData(218L, 8L)]
    [InlineData(254L, 1L)]
    public void T1_Golden_Members(long qid, long bucket)
    {
        Assert.Equal(bucket, CorpusHash.Bucket(qid));
        Assert.True(CorpusHash.IsT1(qid));
    }

    [Fact]
    public void CorpusIdentity_Deterministic()
    {
        Assert.Equal(CorpusIdentity.ComputeId(), CorpusIdentity.ComputeId());
        Assert.Equal(32, CorpusIdentity.ComputeId().Length);
    }
}

public class EntityParserTests
{
    private const string ItemJson =
        "{\"type\":\"item\",\"id\":\"Q31\"," +
        "\"labels\":{\"en\":{\"language\":\"en\",\"value\":\"Belgium\"},\"nb\":{\"language\":\"nb\",\"value\":\"Belgia\"},\"xx\":{\"language\":\"xx\",\"value\":\"ignored\"}}," +
        "\"aliases\":{\"en\":[{\"language\":\"en\",\"value\":\"Belgie\"},{\"language\":\"en\",\"value\":\"Belgique\"},{\"language\":\"en\",\"value\":\"Belgie\"}]," +
        "\"nb\":[{\"language\":\"nb\",\"value\":\"Belgien\"}]}," +
        "\"claims\":{" +
        "\"P31\":[{\"mainsnak\":{\"snaktype\":\"value\",\"property\":\"P31\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"numeric-id\":6256,\"id\":\"Q6256\"},\"type\":\"wikibase-entityid\"}}}," +
        "{\"mainsnak\":{\"snaktype\":\"value\",\"property\":\"P31\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"numeric-id\":6256,\"id\":\"Q6256\"},\"type\":\"wikibase-entityid\"}}}]," +
        "\"P279\":[{\"mainsnak\":{\"snaktype\":\"value\",\"property\":\"P279\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"numeric-id\":184,\"id\":\"Q184\"},\"type\":\"wikibase-entityid\"}}}]}}";

    [Fact]
    public void Parser_ValidItem_Semantics()
    {
        var res = EntityParser.Parse(ItemJson);
        Assert.Equal(EntityOutcome.Item, res.Outcome);
        Assert.True(res.CountsAsSourceRecord);
        var item = res.Item!;
        Assert.Equal(31L, item.Qid);
        Assert.True(item.LabelEnPresent);
        Assert.True(item.LabelNbPresent);
        Assert.Equal("Belgium", item.LabelEnValue);
        Assert.Equal(2, item.AliasEn.Count);
        Assert.Contains("Belgie", item.AliasEn);
        Assert.Contains("Belgique", item.AliasEn);
        Assert.Single(item.AliasNb);
        Assert.Equal(new[] { 6256L }, item.P31Targets);
        Assert.Equal(new[] { 184L }, item.P279Targets);
    }

    [Fact]
    public void Parser_DuplicateP31_Eliminated()
    {
        var res = EntityParser.Parse(ItemJson);
        Assert.Equal(1, res.Item!.P31Targets.Count);
    }

    [Theory]
    [InlineData("{\"type\":\"property\",\"id\":\"P31\"}", EntityOutcome.NonItem, true)]
    [InlineData("{\"type\":\"item\",\"id\":\"Q0\"}", EntityOutcome.Malformed, true)]
    [InlineData("{\"type\":\"item\"}", EntityOutcome.Malformed, false)]
    [InlineData("{\"type\":\"property\",\"id\":\"Q5\"}", EntityOutcome.Malformed, true)]
    [InlineData("{\"type\":\"foo\",\"id\":\"Q5\"}", EntityOutcome.Malformed, true)]
    [InlineData("{\"missing\":true}", EntityOutcome.Missing, false)]
    [InlineData("{\"id\":\"Q5\"}", EntityOutcome.Malformed, true)]
    public void Parser_Classification(string line, EntityOutcome expected, bool countsAsSource)
    {
        var res = EntityParser.Parse(line);
        Assert.Equal(expected, res.Outcome);
        Assert.Equal(countsAsSource, res.CountsAsSourceRecord);
    }

    [Fact]
    public void Parser_PropertyNeverProjected()
    {
        var res = EntityParser.Parse("{\"type\":\"property\",\"id\":\"P31\"}");
        Assert.Equal(EntityOutcome.NonItem, res.Outcome);
        Assert.Null(res.Item);
    }

    [Fact]
    public void Parser_TrailingComma_Stripped()
    {
        var res = EntityParser.Parse(ItemJson + ",");
        Assert.Equal(EntityOutcome.Item, res.Outcome);
    }

    [Fact]
    public void Parser_InvalidJson_Malformed()
    {
        Assert.Equal(EntityOutcome.Malformed, EntityParser.Parse("{not json").Outcome);
    }
}

public class PassALogicTests
{
    [Fact]
    public void PresenceFlags_Encoding()
    {
        Assert.Equal(0, PassALogic.PresenceFlags(false, false));
        Assert.Equal(1, PassALogic.PresenceFlags(true, false));
        Assert.Equal(2, PassALogic.PresenceFlags(false, true));
        Assert.Equal(3, PassALogic.PresenceFlags(true, true));
    }

    [Fact]
    public void T2_Endpoints_NoRecursiveClosure()
    {
        var ep = new HashSet<long>();
        PassALogic.AddP279Endpoints(ep, 5, new[] { 10L, 20L });
        PassALogic.AddP279Endpoints(ep, 20, new[] { 30L });
        Assert.Equal(new HashSet<long> { 5, 10, 20, 30 }, ep);
    }

    [Fact]
    public void TierArithmetic()
    {
        var (t2Only, union) = PassALogic.TierArithmetic(1000L, 600L, 100L);
        Assert.Equal(500L, t2Only);
        Assert.Equal(1500L, union);
    }

    [Fact]
    public void Degree_Histogram_Quantiles()
    {
        var hist = new Dictionary<long, long>
        {
            [1] = 90,
            [2] = 9,
            [3] = 1,
        };
        var s = PassALogic.DegreeSummary(hist);
        Assert.Equal(100L, s.ItemCount);
        Assert.Equal(1L, s.Min);
        Assert.Equal(3L, s.Max);
        Assert.Equal(1.0, s.Median);
        Assert.Equal(1.0, s.P90);
        Assert.Equal(2.0, s.P95);
        Assert.Equal(2.0, s.P99);
    }

    [Fact]
    public void Degree_OverflowBucket()
    {
        var hist = new Dictionary<long, long>();
        PassALogic.BumpDegree(hist, 5);
        PassALogic.BumpDegree(hist, PassALogic.OverflowDegree + 1234);
        PassALogic.BumpDegree(hist, PassALogic.OverflowDegree + 1);
        Assert.Equal(2L, hist[PassALogic.OverflowDegree]);
    }
}

public class PersistenceAndStateTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mimir-cat-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    [Fact]
    public void T2Persistence_RoundTrip_SortedDeterministic()
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "t2.bin");
        long[] sorted = new[] { 9L, 1L, 5L, 1L, 3L, 9L }.OrderBy(x => x).Distinct().ToArray();
        long bytes = T2Persistence.WriteEndpoints(path, sorted);
        Assert.Equal(sorted.Length * 8L, bytes);
        Assert.Equal(sorted, T2Persistence.ReadEndpoints(path));

        string path2 = Path.Combine(_tmp, "t2b.bin");
        T2Persistence.WriteEndpoints(path2, sorted);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(path2));
    }

    [Fact]
    public void State_Incomplete_NeverComplete()
    {
        Directory.CreateDirectory(_tmp);
        string dir = Path.Combine(_tmp, "run");
        PassA.WriteState(dir, PassAStateKind.Running);
        string state = File.ReadAllText(Path.Combine(dir, "state.json"));
        using var doc = JsonDocument.Parse(state);
        Assert.Equal("Running", doc.RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain("\"Complete\"", state);

        PassA.WriteState(dir, PassAStateKind.Complete);
        state = File.ReadAllText(Path.Combine(dir, "state.json"));
        using var doc2 = JsonDocument.Parse(state);
        Assert.Equal("Complete", doc2.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void ScanCore_SourceLengthMismatch_Fails()
    {
        Directory.CreateDirectory(_tmp);
        string small = Path.Combine(_tmp, "small.gz");
        WriteGz(small, new[] { "[]" });
        Assert.Throws<InvalidDataException>(() =>
            ScanCore.Scan(small, computeSha: false, expectedLength: 155690403548L));
    }

    private static void WriteGz(string path, IEnumerable<string> lines)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        foreach (var l in lines)
        {
            byte[] b = Encoding.UTF8.GetBytes(l + "\n");
            gz.Write(b);
        }
    }
}

public class ScanCoreTests
{
    private const string PropertyLine = "{\"type\":\"property\",\"id\":\"P31\"}";

    private static string ItemLine(string id, long p31, long p279)
    {
        return $"{{\"type\":\"item\",\"id\":\"{id}\",\"labels\":{{\"en\":{{\"language\":\"en\",\"value\":\"v\"}}}}," +
               $"\"aliases\":{{\"en\":[{{\"language\":\"en\",\"value\":\"a1\"}},{{\"language\":\"en\",\"value\":\"a2\"}}]}}," +
               $"\"claims\":{{\"P31\":[{{\"mainsnak\":{{\"snaktype\":\"value\",\"property\":\"P31\",\"datavalue\":{{\"value\":{{\"numeric-id\":{p31}}},\"type\":\"wikibase-entityid\"}}}}}}]," +
               $"\"P279\":[{{\"mainsnak\":{{\"snaktype\":\"value\",\"property\":\"P279\",\"datavalue\":{{\"value\":{{\"numeric-id\":{p279}}},\"type\":\"wikibase-entityid\"}}}}}}]}}}}";
    }

    private static string TempGz(IEnumerable<string> lines)
    {
        string path = Path.Combine(Path.GetTempPath(), "mimir-scan-" + Guid.NewGuid().ToString("N") + ".gz");
        WriteGz(path, lines);
        return path;
    }

    private static void WriteGz(string path, IEnumerable<string> lines)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        foreach (var l in lines)
        {
            byte[] b = Encoding.UTF8.GetBytes(l + "\n");
            gz.Write(b);
        }
    }

    [Fact]
    public void ScanCore_Counters_Synthetic()
    {
        string path = TempGz(new[]
        {
            "[",
            ItemLine("Q31", 6256, 184) + ",",
            PropertyLine + ",",
            "{broken json,",
            "]",
        });
        try
        {
            var res = ScanCore.Scan(path, computeSha: false);
            var t = res.Totals;
            Assert.Equal(1L, t.Items);
            Assert.Equal(1L, t.NonItems);
            Assert.Equal(1L, t.Malformed);
            Assert.Equal(2L, t.SourceRecords); // item + property (broken json has no id)
            Assert.Equal(1L, t.LabelEnPresent);
            Assert.Equal(2L, t.AliasEnStrings);
            Assert.Equal(1L, t.P31Pairs);
            Assert.Equal(1L, t.P279Pairs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScanCore_ReadsInlineSha()
    {
        string path = TempGz(new[] { "[]" });
        try
        {
            var res = ScanCore.Scan(path, computeSha: true);
            Assert.NotNull(res.MeasuredSha256);
            Assert.Equal(64, res.MeasuredSha256!.Length);
            long raw = new FileInfo(path).Length;
            Assert.Equal(raw, res.HashedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FixtureEquivalenceTests
{
    private const string PrefixPath = "/tmp/a-prefix.json.gz";

    [Fact]
    public void BoundedPrefix_Totals_Match_Phase0()
    {
        if (!File.Exists(PrefixPath))
            return; // gate artifact absent: covered by the CLI fixture gate

        var res = ScanCore.Scan(PrefixPath, computeSha: false, expectedLength: null, onItem: null, progress: null);
        var t = res.Totals;
        Assert.Equal(9746L, t.Items);
        Assert.Equal(12551L, t.P31Pairs);
        Assert.Equal(1742L, t.P279Pairs);
        Assert.Equal(15712L, t.AliasEnStrings + t.AliasNbStrings);
    }
}

public class PassAEndToEndFixtureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-passafix-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static string ItemLine(long id, long p31, long p279, bool labelEn, bool labelNb)
    {
        string labels = labelEn || labelNb
            ? "\"labels\":{" + (labelEn ? "\"en\":{\"language\":\"en\",\"value\":\"l\"}" : "") + (labelEn && labelNb ? "," : "") + (labelNb ? "\"nb\":{\"language\":\"nb\",\"value\":\"m\"}" : "") + "}"
            : "\"labels\":{}";
        string claims = "\"P31\":[{\"mainsnak\":{\"snaktype\":\"value\",\"property\":\"P31\",\"datavalue\":{\"value\":{\"numeric-id\":" + p31 + "},\"type\":\"wikibase-entityid\"}}}]," +
                        "\"P279\":[{\"mainsnak\":{\"snaktype\":\"value\",\"property\":\"P279\",\"datavalue\":{\"value\":{\"numeric-id\":" + p279 + "},\"type\":\"wikibase-entityid\"}}}]";
        return "{\"type\":\"item\",\"id\":\"Q" + id + "\"," + labels + ",\"claims\":{" + claims + "}}";
    }

    [Fact]
    public void PassA_EndToEnd_OnSyntheticSource()
    {
        Directory.CreateDirectory(_dir);
        string gz = Path.Combine(_dir, "in.gz");
        // T1 membership in this test is whatever the hash decides; we assert
        // structural invariants instead of absolute T1 counts.
        using (var fs = File.Create(gz))
        using (var stream = new GZipStream(fs, CompressionLevel.Fastest))
        {
            var lines = new List<string>
            {
                "[",
                ItemLine(107, 6256, 184, true, true) + ",",   // Q107 is a known T1 member
                ItemLine(1, 6256, 184, true, false) + ",",    // Q1 is a known T1 non-member (presence row)
                ItemLine(197, 6256, 5, false, true) + ",",    // Q197 is a known T1 member
                "{\"type\":\"property\",\"id\":\"P31\"},",
                "]",
            };
            foreach (var l in lines)
            {
                var b = Encoding.UTF8.GetBytes(l + "\n");
                stream.Write(b);
            }
        }

        string work = Path.Combine(_dir, "pass-a");
        var opts = new PassAOptions { SourcePath = gz, WorkDir = work, IsFixture = true, SkipSha = true };
        PassAEvidence ev = PassA.Run(opts);

        Assert.Equal(3L, ev.Totals.Items);
        Assert.Equal(1L, ev.Totals.NonItems);
        Assert.Equal(2L, ev.T1); // Q107 + Q197 are known T1 members; Q1 is a non-member
        Assert.True(ev.T2 >= 3); // endpoints include the three subjects + objects {6256,184,5}

        // evidence artifacts written; temp aggregation db removed; state Complete
        Assert.True(File.Exists(Path.Combine(work, "evidence.json")));
        Assert.True(File.Exists(Path.Combine(work, "t2-endpoints.bin")));
        Assert.False(File.Exists(Path.Combine(work, "aggregation.sqlite")));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "state.json")));
        Assert.Equal("Complete", state.RootElement.GetProperty("state").GetString());
    }
}
