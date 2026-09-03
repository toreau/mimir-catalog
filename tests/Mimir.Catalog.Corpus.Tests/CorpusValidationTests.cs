using Mimir.Catalog.Corpus;
using Xunit;

namespace Mimir.Catalog.Corpus.Tests;

public class ValidationMathTests
{
    [Fact]
    public void ItemFractionDiagnostic_MatchesAcceptedReference()
    {
        var (n, expected, diff, fraction, z) = ValidationMath.ItemFractionDiagnostic(
            CorpusValidationExpectations.FullItems, CorpusValidationExpectations.T1, 0.025);
        Assert.Equal(121_439_429L, n);
        Assert.Equal(3_036_124L, CorpusValidationExpectations.T1);
        Assert.Equal(3_035_985.725, expected, 3);
        Assert.Equal(138L, diff);
        Assert.Equal(0.0250011, fraction, 7); // 2.50011 %
        Assert.Equal(0.08, z, 2);              // reference binomial z ~ 0.08 (diagnostic only)
    }

    [Fact]
    public void GlobalShare_EffectSizes()
    {
        var share = ValidationMath.GlobalShare(CorpusValidationExpectations.FullP31, CorpusValidationExpectations.InstanceOfRows);
        Assert.Equal(0.025003, share.ShareFraction, 6); // 2.50030 %
    }

    [Fact]
    public void DegreeStats_IncludesZeroEntries()
    {
        var hist = new Dictionary<long, long> { [0] = 10, [1] = 90 };
        var s = ValidationMath.DegreeStats(hist);
        Assert.Equal(100L, s.Count);
        Assert.Equal(0L, s.Min);
        Assert.Equal(1L, s.Median);
        Assert.Equal(1L, s.P90);
    }
}

public class ConceptIndexTests
{
    [Fact]
    public void DuplicateQid_Rejected()
    {
        var entries = new List<ConceptIndex.Entry>
        {
            new(5, 0, false, true),
            new(5, 1, true, false),
        };
        Assert.Throws<InvalidDataException>(() => new ConceptIndex(entries));
    }

    [Fact]
    public void Lookup_ReturnsFlagsAndOrdinal()
    {
        var entries = new List<ConceptIndex.Entry>
        {
            new(1, 0, true, false),
            new(184, 1, false, true),
            new(200, 2, false, true),
        };
        var idx = new ConceptIndex(entries);
        Assert.True(idx.TryGet(184, out var e));
        Assert.Equal(1, e.Ordinal);
        Assert.False(e.InT1);
        Assert.True(e.InT2);
        Assert.False(idx.TryGet(999, out _));
    }
}

public class AnchorFixtureTests
{
    private static string FindFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "validation", "phase0-anchors-v1.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("phase0-anchors-v1.json not found above test output dir");
    }

    [Fact]
    public void FrozenFixture_Parses_WithAuthoritativeSemantics()
    {
        var ev = new CorpusValidationEvidence();
        AnchorFixture fixture = AnchorLoader.Load(FindFixture(), ev);
        Assert.Empty(ev.FailedGates); // set sizes verified inside loader
        Assert.Equal(132, fixture.QidsOf("resolvedGold").Count);
        Assert.Equal(63, fixture.QidsOf("outsideCoverage").Count);
        Assert.Equal(209, fixture.QidsOf("goldUnion").Count);
        Assert.Equal(24, fixture.QidsOf("acquisitionSeedsAll").Count);
        Assert.Equal(250, fixture.Cases.Count);
    }

    [Fact]
    public void SurfaceQids_ContainsResolvedGoldAndCandidates()
    {
        AnchorFixture fixture = AnchorLoader.Load(FindFixture(), new CorpusValidationEvidence());
        var surface = fixture.SurfaceQids();
        Assert.All(fixture.QidsOf("resolvedGold"), q => Assert.Contains(q, surface));
    }
}

public class ValidatorGateRegressionTests
{
    // ---- edge ordering across simulated row-group boundaries ----
    [Fact]
    public void TargetOrder_StrictlyAscending_SameSubjectAcrossRowGroups_Passes()
    {
        var st = new ValidationGates.TargetOrderState();
        // "row group" 1
        Assert.Null(st.Step(5, 1));
        Assert.Null(st.Step(5, 2));
        // "row group" 2 begins mid-subject
        Assert.Null(st.Step(5, 3));
        Assert.Null(st.Step(6, 10)); // new subject, new group
    }

    [Fact]
    public void TargetOrder_DuplicateAcrossRowGroupBoundary_Fails()
    {
        var st = new ValidationGates.TargetOrderState();
        Assert.Null(st.Step(5, 7));
        Assert.Null(st.Step(5, 9));   // end of row group 1
        Assert.NotNull(st.Step(5, 9)); // first target of row group 2 == previous -> duplicate
    }

    [Fact]
    public void TargetOrder_DecreasingAcrossRowGroupBoundary_Fails()
    {
        var st = new ValidationGates.TargetOrderState();
        Assert.Null(st.Step(5, 9));   // end of row group 1
        Assert.NotNull(st.Step(5, 4)); // first target of row group 2 decreases
    }

    // ---- lexical ordering with empty raw values ----
    [Fact]
    public void LexicalOrder_EnforcedAfterEmptyPreviousValue()
    {
        var st = new ValidationGates.LexicalOrderState();
        Assert.Null(st.Step("nb", "label", ""));       // previous raw value is ""
        // en comes before nb; going nb -> en must violate ordering even though previous value was empty
        Assert.NotNull(st.Step("en", "label", "x"));
    }

    [Fact]
    public void LexicalOrder_EmptyValueWithinEnAliases_Allowed()
    {
        var st = new ValidationGates.LexicalOrderState();
        Assert.Null(st.Step("en", "alias", ""));
        Assert.Null(st.Step("en", "alias", "a"));
        Assert.NotNull(st.Step("en", "alias", "")); // "" < "a" is fine forward; "a" then "" violates
    }

    // ---- concept flags/tail/hash gates ----
    private static List<string> ConceptErrors(long[] qids, bool[] t1, bool[] t2, int tail)
    {
        var errs = new List<string>();
        ValidationGates.CheckConcept(qids, t1, t2, tail, errs);
        return errs;
    }

    [Fact]
    public void ConceptGates_RejectFalseFalse()
    {
        var errs = ConceptErrors(new long[] { 1 }, new[] { false }, new[] { false }, 0);
        Assert.Contains(errs, e => e.Contains("(false,false)"));
    }

    [Fact]
    public void ConceptGates_RejectInT1HashMismatch()
    {
        // Q1 is a golden non-member
        var errs = ConceptErrors(new long[] { 1 }, new[] { true }, new[] { false }, 0);
        Assert.Contains(errs, e => e.Contains("hash non-member"));
    }

    [Fact]
    public void ConceptGates_HashQualifiedOutsideTail_Rejected()
    {
        // Q107 is a golden member; place it as a non-tail InT1=false row -> must fail
        var errs = ConceptErrors(new long[] { 107, 164 }, new[] { false, false }, new[] { true, true }, 1);
        Assert.Contains(errs, e => e.Contains("outside declared tail"));
    }

    [Fact]
    public void ConceptGates_HashQualifiedTail_Allowed()
    {
        var errs = ConceptErrors(new long[] { 164 }, new[] { false }, new[] { true }, 1);
        Assert.DoesNotContain(errs, e => e.Contains("tail"));
    }

    [Fact]
    public void ConceptGates_NonHashTail_Allowed()
    {
        var errs = ConceptErrors(new long[] { 1 }, new[] { false }, new[] { true }, 1);
        Assert.DoesNotContain(errs, e => e.Contains("tail"));
    }

    // ---- fixture corruption ----
    private static string FindFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "validation", "phase0-anchors-v1.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("phase0-anchors-v1.json not found above test output dir");
    }

    [Fact]
    public void FixtureCorruption_SchemaMismatch_Fails()
    {
        string src = FindFixture();
        string dir = Path.Combine(Path.GetTempPath(), "mimir-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string copy = Path.Combine(dir, "bad.json");
            File.WriteAllText(copy, File.ReadAllText(src).Replace("\"phase0-anchors-v1\"", "\"wrong-schema\""));
            var ev = new CorpusValidationEvidence();
            AnchorLoader.Load(copy, ev);
            Assert.Contains(ev.FailedGates, g => g.Contains("schema"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Runner_MissingCorpus_ProducesHold()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mimir-nocorpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string verdict = new CorpusValidationRunner(dir, "/nonexistent/fixture.json").Run();
            Assert.Equal("HOLD", verdict);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
