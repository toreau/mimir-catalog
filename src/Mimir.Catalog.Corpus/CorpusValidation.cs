using System.Text;
using System.Text.Json;
using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Corpus;

/// <summary>Compact Concept lookup: packed entries sorted by QID, retaining the materialization ordinal.</summary>
public sealed class ConceptIndex
{
    public readonly struct Entry
    {
        public readonly long Qid;
        public readonly int Ordinal;
        public readonly bool InT1;
        public readonly bool InT2;

        public Entry(long qid, int ordinal, bool inT1, bool inT2)
        {
            Qid = qid;
            Ordinal = ordinal;
            InT1 = inT1;
            InT2 = inT2;
        }
    }

    private readonly Entry[] _sorted;

    public ConceptIndex(IReadOnlyList<Entry> entries)
    {
        var copy = new Entry[entries.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = entries[i];
        Array.Sort(copy, (a, b) => a.Qid.CompareTo(b.Qid));
        for (int i = 1; i < copy.Length; i++)
            if (copy[i].Qid == copy[i - 1].Qid)
                throw new InvalidDataException($"duplicate Concept QID {copy[i].Qid}");
        _sorted = copy;
    }

    public int Count => _sorted.Length;

    public bool TryGet(long qid, out Entry entry)
    {
        int lo = 0, hi = _sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_sorted[mid].Qid < qid) lo = mid + 1;
            else if (_sorted[mid].Qid > qid) hi = mid - 1;
            else { entry = _sorted[mid]; return true; }
        }
        entry = default;
        return false;
    }
}

/// <summary>Pure statistics helpers used by validation and tests.</summary>
public static class ValidationMath
{
    public static (long N, double Expected, long Difference, double Fraction, double Z) ItemFractionDiagnostic(long n, long t1, double p)
    {
        double expected = n * p;
        double fraction = n == 0 ? 0 : (double)t1 / n;
        double se = Math.Sqrt(n * p * (1 - p));
        double z = se == 0 ? 0 : (t1 - expected) / se;
        return (n, expected, t1 - (long)Math.Round(expected), fraction, z);
    }

    public sealed record Share(long Full, long Sample, double ShareFraction, double PpDiff, double RelDiff)
    {
        public object? ToJson() => new { full = Full, sample = Sample, shareFraction = Math.Round(ShareFraction * 100, 6), ppDiff = Math.Round(PpDiff, 6), relDiff = Math.Round(RelDiff * 100, 6) };
    }

    public static Share GlobalShare(long full, long sample)
    {
        double f = full == 0 ? 0 : (double)sample / full;
        return new Share(full, sample, f, (f - CorpusValidationExpectations.SampleFraction) * 100.0,
            full == 0 ? 0 : (f - CorpusValidationExpectations.SampleFraction) / CorpusValidationExpectations.SampleFraction);
    }

    public static (long Count, long Min, long Max, double Median, double P90, double P95, double P99, double Mean) DegreeStats(Dictionary<long, long> hist)
    {
        if (hist.Count == 0) return (0, 0, 0, 0, 0, 0, 0, 0);
        long total = hist.Values.Sum();
        long min = hist.Keys.Min(), max = hist.Keys.Max();
        double mean = (double)hist.Sum(kv => (double)kv.Key * kv.Value) / total;
        double Quantile(double q)
        {
            long target = (long)Math.Ceiling(q * total);
            long acc = 0;
            foreach (var kv in hist.OrderBy(k => k.Key)) { acc += kv.Value; if (acc >= target) return kv.Key; }
            return max;
        }
        return (total, min, max, Quantile(0.50), Quantile(0.90), Quantile(0.95), Quantile(0.99), mean);
    }
}

/// <summary>Frozen Phase-0 anchor fixture model.</summary>
public sealed class AnchorFixture
{
    public sealed class SetDoc { public required string Name { get; init; } public required List<string> Qids { get; init; } }
    public sealed class Case
    {
        public required string Id { get; init; }
        public required string Category { get; init; }
        public required string Language { get; init; }
        public required string Term { get; init; }
        public required string Status { get; init; }
        public string? Qid { get; init; }
        public string? NameType { get; init; }
        public string? EvidenceClass { get; init; }
        public List<string>? CandidateQids { get; init; }
    }

    public required string Schema { get; init; }
    public required string Phase0Commit { get; init; }
    public required List<SourceDoc> Sources { get; init; }
    public required List<SetDoc> Sets { get; init; }
    public required List<Case> Cases { get; init; }

    public sealed class SourceDoc { public required string Path { get; init; } public required string Sha256 { get; init; } }

    public HashSet<long> QidsOf(string name) => Sets.First(s => s.Name == name).Qids.Select(QidValue).ToHashSet();
    private static long QidValue(string q) => long.Parse(q[1..], System.Globalization.CultureInfo.InvariantCulture);
    public HashSet<long> AllAnchorQids() => Sets.SelectMany(s => s.Qids).Select(QidValue).ToHashSet();
    public HashSet<long> SurfaceQids() => Cases
        .Where(c => c.Qid != null || c.CandidateQids != null)
        .SelectMany(c => c.CandidateQids ?? (c.Qid == null ? Enumerable.Empty<string>() : new[] { c.Qid! }))
        .Select(QidValue).ToHashSet();
}

/// <summary>
/// Loads and validates the locally frozen Phase-0 anchor fixture. No runtime
/// coupling to the sibling Phase-0 repository.
/// </summary>
public static class AnchorLoader
{
    public const string ExpectedSchema = "phase0-anchors-v1";
    public const string ExpectedCommit = "b65f0f472d5333e84dd59b7bb2bb3fef0e0248f8";
    public const string SourceTargets = "evaluation/0c/0c-targets.json";
    public const string SourceTargetsSha = "75f592a35b1dc3bea7a61cf53fe9bf1ab6a000bc8ec23e3625eed26672fe6394";
    public const string SourceCases = "evaluation/gold/source/gold-v0-cases.jsonl";
    public const string SourceCasesSha = "a806aecf6a208400fbee360632824679b95546beba6cb26baf46eda1ad6737c2";

    public static AnchorFixture Load(string path, CorpusValidationEvidence ev)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (root.TryGetProperty("schema", out var sch) && sch.GetString() == ExpectedSchema) { } else ev.Fail("fixture schema mismatch");
        string commit = root.GetProperty("phase0_commit").GetString()!;
        if (commit != ExpectedCommit) ev.Fail("fixture phase0_commit mismatch");

        var sources = new List<AnchorFixture.SourceDoc>();
        foreach (var s in root.GetProperty("sources").EnumerateArray())
            sources.Add(new AnchorFixture.SourceDoc { Path = s.GetProperty("path").GetString()!, Sha256 = s.GetProperty("sha256").GetString()! });
        bool targetsOk = sources.Any(s => s.Path == SourceTargets && s.Sha256 == SourceTargetsSha);
        bool casesOk = sources.Any(s => s.Path == SourceCases && s.Sha256 == SourceCasesSha);
        if (!targetsOk) ev.Fail("fixture provenance 0c-targets missing/mismatched");
        if (!casesOk) ev.Fail("fixture provenance gold-v0-cases missing/mismatched");

        var sets = new List<AnchorFixture.SetDoc>();
        var setNames = new HashSet<string>();
        foreach (var p in root.GetProperty("sets").EnumerateObject())
        {
            var qids = p.Value.EnumerateArray().Select(e => e.GetString()!).ToList();
            foreach (var q in qids)
                if (!Qid.IsValidItemId(q)) { ev.Fail($"invalid QID {q} in set {p.Name}"); break; }
            if (qids.Distinct().Count() != qids.Count) ev.Fail($"duplicate QID in set {p.Name}");
            setNames.Add(p.Name);
            sets.Add(new AnchorFixture.SetDoc { Name = p.Name, Qids = qids });
        }

        var cases = new List<AnchorFixture.Case>();
        var caseIds = new HashSet<string>();
        foreach (var c in root.GetProperty("goldCases").EnumerateArray())
        {
            string id = c.GetProperty("id").GetString()!;
            if (!caseIds.Add(id)) ev.Fail($"duplicate gold case id {id}");
            List<string>? cands = null;
            if (c.TryGetProperty("candidateQids", out var ca)) cands = ca.EnumerateArray().Select(e => e.GetString()!).ToList();
            cases.Add(new AnchorFixture.Case
            {
                Id = id,
                Category = c.GetProperty("category").GetString()!,
                Language = c.GetProperty("language").GetString()!,
                Term = c.GetProperty("term").GetString()!,
                Status = c.GetProperty("status").GetString()!,
                Qid = c.TryGetProperty("qid", out var q) ? q.GetString() : null,
                NameType = c.TryGetProperty("nameType", out var nt) ? nt.GetString() : null,
                EvidenceClass = c.TryGetProperty("evidenceClass", out var ec) ? ec.GetString() : null,
                CandidateQids = cands,
            });
        }
        var fixture = new AnchorFixture { Schema = ExpectedSchema, Phase0Commit = commit, Sources = sources, Sets = sets, Cases = cases };

        var exp = new Dictionary<string, int> {
            ["resolvedGold"] = 132, ["outsideCoverage"] = 63, ["unclassifiedResolvable"] = 63, ["goldUnion"] = 209,
            ["discoveryEvaluationTargets"] = 200, ["ambiguousCand"] = 108,
            ["acquisitionSeedsAll"] = 24, ["taxonomyRoots"] = 12, ["curatedAllowlists"] = 12,
            ["evaluationSeedOverlap"] = 9,
        };
        foreach (var (name, count) in exp)
        {
            var set = fixture.Sets.FirstOrDefault(s => s.Name == name);
            if (set == null || set.Qids.Count != count) { ev.Fail($"anchor set {name} does not match authoritative semantics"); break; }
        }
        if (fixture.Cases.Count != 250) ev.Fail($"anchor fixture cases {fixture.Cases.Count} != 250");

        // Phase-0 semantics: outsideCoverage == unclassifiedResolvable.
        if (fixture.Sets.Any(s => s.Name == "outsideCoverage") && fixture.Sets.Any(s => s.Name == "unclassifiedResolvable"))
        {
            var o = fixture.QidsOf("outsideCoverage");
            var u = fixture.QidsOf("unclassifiedResolvable");
            if (!o.SetEquals(u)) ev.Fail("outsideCoverage != unclassifiedResolvable");
        }
        return fixture;
    }
}

/// <summary>Records validation results.</summary>
public sealed class CorpusValidationEvidence
{
    public const string ValidationVersion = "1";

    public List<string> Warnings { get; } = new();
    public List<string> Observations { get; } = new();
    public List<string> FailedGates { get; } = new();

    public long ConceptRows;
    public long UniqueConcepts;
    public long T1Concepts;
    public long T2Concepts;
    public long T1IntersectT2;
    public long T2OnlyConcepts;
    public long TailCount;
    public long TailHashQualified;
    public List<string> TailHashQualifiedQids { get; } = new();

    public long LexicalRows;
    public long T1EnLabels, T1NbLabels, T1EnAliases, T1NbAliases;
    public long T2OnlyEnLabels, T2OnlyNbLabels;

    public long InstanceOfRows;
    public long InstanceOfDistinctSubjects;
    public long SubclassOfRows;
    public long SubclassOfDistinctSubjects;
    public long SubclassOfDistinctObjects;
    public long T2EndpointsInSubclassOf;

    public string? Verdict { get; set; }

    public void Fail(string gate) => FailedGates.Add(gate);
    public bool HasFailures => FailedGates.Count > 0;
}

/// <summary>Phase 1.1A.2c corpus validation runner.</summary>
public sealed class CorpusValidationRunner
{
    private readonly string _corpusRoot;
    private readonly string _passB;
    private readonly string _passA;
    private readonly string _fixturePath;
    private readonly CorpusValidationEvidence _ev = new();

    private long[] _t2 = Array.Empty<long>();
    private ConceptIndex _concept = new(Array.Empty<ConceptIndex.Entry>());
    private readonly Dictionary<string, object?> _inputIdentities = new();
    private string? _runId;
    private string _staging = "";
    private readonly System.Diagnostics.Stopwatch _sw = new();

    public CorpusValidationRunner(string corpusRoot, string fixturePath)
    {
        _corpusRoot = corpusRoot;
        _fixturePath = fixturePath;
        _passB = Path.Combine(corpusRoot, "pass-b");
        _passA = Path.Combine(corpusRoot, "pass-a");
    }

    public CorpusValidationEvidence Evidence => _ev;

    public string Run()
    {
        _runId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        _staging = Path.Combine(_corpusRoot, $"pass-c-staging-{_runId}");
        Directory.CreateDirectory(_staging);
        _sw.Start();

        if (!Preflight())
        {
            _ev.Verdict = "HOLD";
            Finalize(false);
            return "HOLD";
        }
        _concept = ValidateConcept();
        if (_ev.HasFailures) { _ev.Verdict = "HOLD"; Finalize(false); return "HOLD"; }
        CheckConceptT2Equality();

        var anchors = AnchorLoader.Load(_fixturePath, _ev);
        var surfaces = new Dictionary<long, Dictionary<string, (HashSet<string> Labels, HashSet<string> Aliases)>>();
        foreach (long q in anchors.SurfaceQids()) surfaces[q] = new Dictionary<string, (HashSet<string>, HashSet<string>)>();
        ValidateLexical(surfaces);

        var instance = ValidateInstanceOf();
        var subclass = ValidateSubclassOf();
        if (_ev.HasFailures) { _ev.Verdict = "HOLD"; Finalize(false); return "HOLD"; }

        var diagnostics = BuildDiagnostics(instance, subclass);
        var anchorCoverage = ComputeAnchorCoverage(anchors);
        var lexicalSurface = ComputeLexicalSurface(anchors, surfaces);
        var ambiguous = ComputeAmbiguousContinuity(anchors, surfaces);

        _sw.Stop();
        AddObservations(lexicalSurface);
        _ev.Verdict = "GO";
        Finalize(true, anchors, anchorCoverage, lexicalSurface, ambiguous, diagnostics);
        return "GO";
    }

    private void AddObservations(Dictionary<string, object?> lexicalSurface)
    {
        if (_ev.TailHashQualified == 0)
            _ev.Observations.Add("tail_hash_qualified_count = 0 (the real 20-row tail did not exercise the hash-qualified exception)");
        if (lexicalSurface.TryGetValue("totals", out var t) && t is Dictionary<string, long> totals && totals.GetValueOrDefault("concept_present_alias_surface", 0) == 0)
            _ev.Observations.Add("resolved WikidataAlias raw-surface hits = 0 (continuity result; T2-only concepts are intentionally label-only)");
    }

    // ---------- publication ----------
    private void Finalize(bool go, AnchorFixture? anchors = null,
        Dictionary<string, object?>? anchorCoverage = null,
        Dictionary<string, object?>? lexicalSurface = null,
        Dictionary<string, object?>? ambiguous = null,
        Dictionary<string, object?>? diagnostics = null)
    {
        if (go)
        {
            WriteEvidenceDoc(anchors!, anchorCoverage!, lexicalSurface!, ambiguous!, diagnostics!);
            WriteState("Complete");
            string passC = Path.Combine(_corpusRoot, "pass-c");
            if (Directory.Exists(passC))
                throw new InvalidDataException($"published pass-c already exists: {passC} (no silent overwrite)");
            Directory.Move(_staging, passC);
        }
        else
        {
            // Preserve staging diagnostics; never masquerade as accepted pass-c.
            WriteState("Hold");
            string passC = Path.Combine(_corpusRoot, "pass-c");
            if (Directory.Exists(passC))
                Directory.Move(passC, Path.Combine(_corpusRoot, $"pass-c.superseded-{_runId}"));
        }
    }

    private void WriteState(string state)
    {
        string path = Path.Combine(_staging, "validation.state.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { state, run_id = _runId, utc = DateTime.UtcNow }));
        File.Move(tmp, path, true);
    }

    private void WriteEvidenceDoc(AnchorFixture anchors,
        Dictionary<string, object?> anchorCoverage,
        Dictionary<string, object?> lexicalSurface,
        Dictionary<string, object?> ambiguous,
        Dictionary<string, object?> diagnostics)
    {
        var doc = new Dictionary<string, object?>
        {
            ["validationVersion"] = CorpusValidationEvidence.ValidationVersion,
            ["runId"] = _runId,
            ["verdict"] = _ev.Verdict,
            ["completed"] = true,
            ["failedGates"] = _ev.FailedGates,
            ["warnings"] = _ev.Warnings,
            ["observations"] = _ev.Observations,
            ["inputs"] = _inputIdentities,
            ["integrity"] = new Dictionary<string, object?>
            {
                ["concept"] = new { rows = _ev.ConceptRows, unique = _ev.UniqueConcepts, t1 = _ev.T1Concepts, t2 = _ev.T2Concepts, t1IntersectT2 = _ev.T1IntersectT2, t2Only = _ev.T2OnlyConcepts },
                ["tail"] = new { tailCount = _ev.TailCount, tailHashQualified = _ev.TailHashQualified, tailHashQualifiedQids = _ev.TailHashQualifiedQids },
                ["conceptT2Exact"] = true,
                ["lexical"] = new { rows = _ev.LexicalRows, t1EnLabels = _ev.T1EnLabels, t1NbLabels = _ev.T1NbLabels, t1EnAliases = _ev.T1EnAliases, t1NbAliases = _ev.T1NbAliases, t2OnlyEnLabels = _ev.T2OnlyEnLabels, t2OnlyNbLabels = _ev.T2OnlyNbLabels },
                ["instanceOf"] = new { rows = _ev.InstanceOfRows, distinctSubjects = _ev.InstanceOfDistinctSubjects },
                ["subclassOf"] = new { rows = _ev.SubclassOfRows, distinctSubjects = _ev.SubclassOfDistinctSubjects, distinctObjects = _ev.SubclassOfDistinctObjects, endpointUnion = _ev.T2EndpointsInSubclassOf },
            },
            ["diagnostics"] = diagnostics,
            ["anchors"] = new Dictionary<string, object?>
            {
                ["qidCoverage"] = anchorCoverage,
                ["lexicalSurface"] = lexicalSurface,
                ["ambiguousContinuity"] = ambiguous,
            },
            ["operational"] = new Dictionary<string, object?>
            {
                ["wall_seconds"] = Math.Round(_sw.Elapsed.TotalSeconds, 3),
                ["managed_rss_note"] = "no in-process external RSS; authoritative external RSS from /usr/bin/time -l wrapper in run log",
            },
        };
        string path = Path.Combine(_staging, "validation.json");
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- preflight (captures identities) ----------
    private bool Preflight()
    {
        string statePath = Path.Combine(_passB, "pass-b.state.json");
        bool complete = false;
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                complete = doc.RootElement.TryGetProperty("state", out var s) && s.GetString() == "Complete";
            }
            catch (JsonException) { }
        }
        if (!complete) { _ev.Fail("pass-b state not Complete"); return false; }

        string matPath = Path.Combine(_passB, "materialization.json");
        if (!File.Exists(matPath)) { _ev.Fail("materialization.json missing"); return false; }
        bool ok = true;
        var artifacts = new Dictionary<string, object?>();
        foreach (var (key, file) in new[] { ("concept", "concept.parquet"), ("lexical_entry", "lexical_entry.parquet"), ("instance_of", "instance_of.parquet"), ("subclass_of", "subclass_of.parquet") })
        {
            string path = Path.Combine(_passB, file);
            long size = new FileInfo(path).Length;
            string sha = Sha256(path);
            artifacts[key] = new { bytes = size, sha256 = sha };
            if (size != MatLong(matPath, $"artifacts.{key}.byteSize")) { _ev.Fail($"{key} size mismatch"); ok = false; }
            if (sha != MatStr(matPath, $"artifacts.{key}.sha256")) { _ev.Fail($"{key} SHA mismatch"); ok = false; }
        }
        string t2Path = Path.Combine(_passA, "t2-endpoints.bin");
        long t2Size = new FileInfo(t2Path).Length;
        string t2Sha = Sha256(t2Path);
        if (t2Size != PassBIdentity.ExpectedT2Bytes || t2Sha != PassBIdentity.ExpectedT2Sha256)
        {
            _ev.Fail("t2 artifact identity mismatch");
            ok = false;
        }
        else
        {
            byte[] t2b = File.ReadAllBytes(t2Path);
            _t2 = new long[t2b.Length / 8];
            for (long i = 0; i < _t2.Length; i++)
                _t2[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(t2b.AsSpan((int)(i * 8), 8));
        }

        var fixture = new FileInfo(_fixturePath);
        _inputIdentities["corpusRoot"] = _corpusRoot;
        _inputIdentities["passBState"] = "Complete";
        _inputIdentities["t2"] = new { count = PassBIdentity.ExpectedT2Count, bytes = t2Size, sha256 = t2Sha };
        _inputIdentities["parquetArtifacts"] = artifacts;
        _inputIdentities["materializationPath"] = matPath;
        _inputIdentities["phase0Fixture"] = new { path = _fixturePath, sha256 = fixture.Exists ? Sha256(_fixturePath) : "missing" };
        return ok;
    }

    private static long MatLong(string matPath, string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(matPath));
        var el = doc.RootElement;
        foreach (var part in jsonPath.Split('.'))
            el = el.GetProperty(part);
        return el.GetInt64();
    }

    private static string MatStr(string matPath, string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(matPath));
        var el = doc.RootElement;
        foreach (var part in jsonPath.Split('.'))
            el = el.GetProperty(part);
        return el.GetString()!;
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
    }

    // ---------- Concept ----------
    private ConceptIndex ValidateConcept()
    {
        string path = Path.Combine(_passB, "concept.parquet");
        var qids = ParquetRead.ReadLongs(path, 0);
        var in1 = ParquetRead.ReadBools(path, 1);
        var in2 = ParquetRead.ReadBools(path, 2);
        _ev.ConceptRows = qids.Length;
        Require(CorpusValidationExpectations.ConceptRows, qids.Length, "Concept rows");

        var gateErrors = new List<string>();
        var chk = ValidationGates.CheckConcept(qids, in1, in2, (int)CorpusValidationExpectations.UnobservedTail, gateErrors);
        foreach (var e in gateErrors) _ev.Fail(e);
        _ev.TailCount = CorpusValidationExpectations.UnobservedTail;
        _ev.TailHashQualified = chk.TailHashQualified;
        _ev.TailHashQualifiedQids.AddRange(chk.TailHashQualifiedQids);

        var entries = new List<ConceptIndex.Entry>(qids.Length);
        long t1 = 0, t2 = 0, cap = 0;
        for (int i = 0; i < qids.Length; i++)
        {
            if (in1[i]) t1++;
            if (in2[i]) t2++;
            if (in1[i] && in2[i]) cap++;
            entries.Add(new ConceptIndex.Entry(qids[i], i, in1[i], in2[i]));
        }
        _ev.T1Concepts = t1;
        _ev.T2Concepts = t2;
        _ev.T1IntersectT2 = cap;
        _ev.T2OnlyConcepts = t2 - cap;
        Require(CorpusValidationExpectations.T1, t1, "T1");
        Require(CorpusValidationExpectations.T2, t2, "T2");
        Require(CorpusValidationExpectations.T1IntersectT2, cap, "T1∩T2");
        Require(CorpusValidationExpectations.T2Only, t2 - cap, "T2-only");

        ConceptIndex idx;
        try { idx = new ConceptIndex(entries); }
        catch (InvalidDataException ex) { _ev.Fail(ex.Message); return new ConceptIndex(Array.Empty<ConceptIndex.Entry>()); }
        _ev.UniqueConcepts = idx.Count;
        Require(CorpusValidationExpectations.ConceptRows, idx.Count, "Concept unique count");
        return idx;
    }

    private void CheckConceptT2Equality()
    {
        var qids = ParquetRead.ReadLongs(Path.Combine(_passB, "concept.parquet"), 0);
        var in2 = ParquetRead.ReadBools(Path.Combine(_passB, "concept.parquet"), 2);
        var list = new List<long>(qids.Length);
        for (int i = 0; i < qids.Length; i++) if (in2[i]) list.Add(qids[i]);
        list.Sort();
        if (list.Count != _t2.Length) { _ev.Fail($"Concept InT2 count {list.Count} != T2 artifact {_t2.Length}"); return; }
        for (int i = 0; i < list.Count; i++)
            if (list[i] != _t2[i]) { _ev.Fail("Concept/T2 exact set mismatch"); return; }
    }

    // ---------- Lexical ----------
    private void ValidateLexical(Dictionary<long, Dictionary<string, (HashSet<string> Labels, HashSet<string> Aliases)>> surfaces)
    {
        long rows = 0;
        long t1EnLabels = 0, t1NbLabels = 0, t1EnAliases = 0, t1NbAliases = 0;
        long t2EnLabels = 0, t2NbLabels = 0;

        long currentQid = long.MinValue;
        int currentOrdinal = -1;
        HashSet<string>? dupKeys = null;
        int labelCountEn = 0, labelCountNb = 0;
        var order = new ValidationGates.LexicalOrderState();

        ForEachLexicalGroup(cols =>
        {
            var qids = cols.Qids; var lang = cols.Lang; var kind = cols.Kind; var value = cols.Value;
            for (int i = 0; i < qids.Length; i++)
            {
                rows++;
                long q = qids[i];
                bool langOk = lang[i] is "en" or "nb";
                bool kindOk = kind[i] is "label" or "alias";
                if (!langOk) _ev.Fail($"invalid Lang '{lang[i]}'");
                if (!kindOk) _ev.Fail($"invalid LexKind '{kind[i]}'");
                if (!_concept.TryGet(q, out var e))
                {
                    _ev.Fail($"lexical QID Q{q} not in Concept");
                    continue;
                }
                if (q != currentQid)
                {
                    if (currentOrdinal >= 0 && e.Ordinal <= currentOrdinal) _ev.Fail("lexical Concept ordinal not strictly increasing across QID groups");
                    currentQid = q;
                    currentOrdinal = e.Ordinal;
                    dupKeys = new HashSet<string>(StringComparer.Ordinal);
                    labelCountEn = 0;
                    labelCountNb = 0;
                    order.Reset();
                }
                else if (e.Ordinal != currentOrdinal)
                {
                    _ev.Fail($"lexical QID Q{q} reappears at a different ordinal");
                }

                string? oe = order.Step(lang[i], kind[i], value[i]);
                if (oe != null) _ev.Fail(oe);
                if (!dupKeys!.Add(lang[i] + "\u001f" + kind[i] + "\u001f" + value[i])) _ev.Fail($"duplicate lexical row for Q{q}");
                if (kind[i] == "label" && lang[i] == "en" && ++labelCountEn > 1) _ev.Fail($"multiple en labels for Q{q}");
                if (kind[i] == "label" && lang[i] == "nb" && ++labelCountNb > 1) _ev.Fail($"multiple nb labels for Q{q}");

                bool tail = e.Ordinal >= _concept.Count - (int)CorpusValidationExpectations.UnobservedTail;
                if (tail) { _ev.Fail($"lexical row for unobserved tail concept Q{q}"); continue; }
                if (e.InT1)
                {
                    if (kind[i] == "label" && lang[i] == "en") t1EnLabels++;
                    if (kind[i] == "label" && lang[i] == "nb") t1NbLabels++;
                    if (kind[i] == "alias" && lang[i] == "en") t1EnAliases++;
                    if (kind[i] == "alias" && lang[i] == "nb") t1NbAliases++;
                }
                else if (e.InT2)
                {
                    if (kind[i] == "alias") { _ev.Fail($"T2-only alias for Q{q}"); continue; }
                    if (lang[i] == "en") t2EnLabels++;
                    else t2NbLabels++;
                }
                else
                {
                    _ev.Fail($"lexical row for Q{q} with no tier");
                }

                if (surfaces.TryGetValue(q, out var perLang))
                {
                    if (!perLang.TryGetValue(lang[i], out var entry))
                        perLang[lang[i]] = entry = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                    if (kind[i] == "label") entry.Labels.Add(value[i]);
                    else entry.Aliases.Add(value[i]);
                }
            }
        });

        _ev.LexicalRows = rows;
        _ev.T1EnLabels = t1EnLabels;
        _ev.T1NbLabels = t1NbLabels;
        _ev.T1EnAliases = t1EnAliases;
        _ev.T1NbAliases = t1NbAliases;
        _ev.T2OnlyEnLabels = t2EnLabels;
        _ev.T2OnlyNbLabels = t2NbLabels;
        Require(CorpusValidationExpectations.LexicalRows, rows, "LexicalEntry rows");
        Require(CorpusValidationExpectations.T1EnLabels, t1EnLabels, "T1 en labels");
        Require(CorpusValidationExpectations.T1NbLabels, t1NbLabels, "T1 nb labels");
        Require(CorpusValidationExpectations.T1EnAliases, t1EnAliases, "T1 en aliases");
        Require(CorpusValidationExpectations.T1NbAliases, t1NbAliases, "T1 nb aliases");
        Require(CorpusValidationExpectations.T2OnlyEnLabels, t2EnLabels, "T2-only en labels");
        Require(CorpusValidationExpectations.T2OnlyNbLabels, t2NbLabels, "T2-only nb labels");
    }

    // ---------- InstanceOf ----------
    private (Dictionary<long, long> Degrees, Dictionary<long, long> TargetFanout, long DistinctSubjects) ValidateInstanceOf()
    {
        long rows = 0;
        var degrees = new Dictionary<long, long>();
        var fanout = new Dictionary<long, long>();
        long distinctSubjects = 0;
        long currentQid = long.MinValue;
        long groupDegree = 0;
        int currentOrdinal = -1;
        var order = new ValidationGates.TargetOrderState();

        ForEachLongPairs(Path.Combine(_passB, "instance_of.parquet"), (s, t) =>
        {
            for (int i = 0; i < s.Length; i++)
            {
                rows++;
                long subj = s[i];
                if (!_concept.TryGet(subj, out var e))
                {
                    _ev.Fail($"InstanceOf subject Q{subj} not in Concept");
                    continue;
                }
                if (!e.InT1) _ev.Fail($"InstanceOf subject Q{subj} not InT1");
                if (t[i] <= 0) _ev.Fail($"InstanceOf non-positive target for Q{subj}");
                if (subj != currentQid)
                {
                    if (currentQid != long.MinValue) { degrees.TryGetValue(groupDegree, out long g); degrees[groupDegree] = g + 1; }
                    if (currentOrdinal >= 0 && e.Ordinal <= currentOrdinal) _ev.Fail("InstanceOf subject ordinal not strictly increasing across groups");
                    currentQid = subj;
                    currentOrdinal = e.Ordinal;
                    groupDegree = 0;
                    distinctSubjects++;
                }
                else if (e.Ordinal != currentOrdinal)
                {
                    _ev.Fail($"InstanceOf subject Q{subj} reappears at a different ordinal");
                }
                string? oe = order.Step(subj, t[i]);
                if (oe != null) _ev.Fail(oe);
                groupDegree++;
                fanout.TryGetValue(t[i], out long f); fanout[t[i]] = f + 1;
            }
        });
        if (currentQid != long.MinValue) { degrees.TryGetValue(groupDegree, out long g); degrees[groupDegree] = g + 1; }

        _ev.InstanceOfRows = rows;
        _ev.InstanceOfDistinctSubjects = distinctSubjects;
        Require(CorpusValidationExpectations.InstanceOfRows, rows, "InstanceOf rows");
        return (degrees, fanout, distinctSubjects);
    }

    // ---------- SubclassOf ----------
    private (Dictionary<long, long> T1Degrees, long DistinctT1Subjects) ValidateSubclassOf()
    {
        long rows = 0;
        long distinctT1Subjects = 0;
        long currentQid = long.MinValue;
        bool curT1 = false;
        int currentOrdinal = -1;
        long groupDegree = 0;
        var t1Degrees = new Dictionary<long, long>();
        byte[] seenS = new byte[(_t2.Length + 7) / 8];
        byte[] seenO = new byte[(_t2.Length + 7) / 8];
        byte[] seenAny = new byte[(_t2.Length + 7) / 8];
        long seenSCount = 0, seenOCount = 0, seenAnyCount = 0;
        var order = new ValidationGates.TargetOrderState();

        void Mark(long q, byte[] set, ref long count)
        {
            int idx = Array.BinarySearch(_t2, q);
            if (idx < 0) { _ev.Fail($"SubclassOf endpoint Q{q} absent from T2"); return; }
            if ((set[idx >> 3] & (1 << (idx & 7))) == 0) { set[idx >> 3] |= (byte)(1 << (idx & 7)); count++; }
        }

        void FinishGroup(bool inT1)
        {
            if (inT1) { t1Degrees.TryGetValue(groupDegree, out long tg); t1Degrees[groupDegree] = tg + 1; }
        }

        ForEachLongPairs(Path.Combine(_passB, "subclass_of.parquet"), (s, t) =>
        {
            for (int i = 0; i < s.Length; i++)
            {
                rows++;
                long subj = s[i];
                Mark(subj, seenS, ref seenSCount);
                Mark(t[i], seenO, ref seenOCount);
                Mark(subj, seenAny, ref seenAnyCount);
                Mark(t[i], seenAny, ref seenAnyCount);
                if (!_concept.TryGet(subj, out var e))
                {
                    _ev.Fail($"SubclassOf subject Q{subj} not in Concept");
                    continue;
                }
                if (!e.InT2) _ev.Fail($"SubclassOf subject Q{subj} not InT2");
                if (subj != currentQid)
                {
                    if (currentQid != long.MinValue) FinishGroup(curT1);
                    if (currentOrdinal >= 0 && e.Ordinal <= currentOrdinal) _ev.Fail("SubclassOf subject ordinal not strictly increasing");
                    currentQid = subj;
                    currentOrdinal = e.Ordinal;
                    curT1 = e.InT1;
                    if (curT1) distinctT1Subjects++;
                    groupDegree = 0;
                }
                string? oe = order.Step(subj, t[i]);
                if (oe != null) _ev.Fail(oe);
                groupDegree++;
            }
        });
        if (currentQid != long.MinValue) FinishGroup(curT1);

        _ev.SubclassOfRows = rows;
        _ev.SubclassOfDistinctSubjects = seenSCount;
        _ev.SubclassOfDistinctObjects = seenOCount;
        _ev.T2EndpointsInSubclassOf = seenAnyCount;
        Require(CorpusValidationExpectations.SubclassOfRows, rows, "SubclassOf rows");
        Require(CorpusValidationExpectations.P279DistinctSubjects, seenSCount, "P279 distinct subjects");
        Require(CorpusValidationExpectations.P279DistinctObjects, seenOCount, "P279 distinct objects");
        Require(_t2.LongLength, seenAnyCount, "P279 endpoint union == T2");
        return (t1Degrees, distinctT1Subjects);
    }

    // ---------- diagnostics ----------
    private Dictionary<string, object?> BuildDiagnostics(
        (Dictionary<long, long> Degrees, Dictionary<long, long> TargetFanout, long DistinctSubjects) instance,
        (Dictionary<long, long> T1Degrees, long DistinctT1Subjects) subclass)
    {
        var frac = ValidationMath.ItemFractionDiagnostic(CorpusValidationExpectations.FullItems, CorpusValidationExpectations.T1, CorpusValidationExpectations.SampleFraction);

        var shares = new Dictionary<string, object?>
        {
            ["items"] = ValidationMath.GlobalShare(CorpusValidationExpectations.FullItems, CorpusValidationExpectations.T1).ToJson(),
            ["p31_pairs"] = ValidationMath.GlobalShare(CorpusValidationExpectations.FullP31, _ev.InstanceOfRows).ToJson(),
            ["en_labels"] = ValidationMath.GlobalShare(CorpusValidationExpectations.FullLabelEn, _ev.T1EnLabels).ToJson(),
            ["nb_labels"] = ValidationMath.GlobalShare(CorpusValidationExpectations.FullLabelNb, _ev.T1NbLabels).ToJson(),
            ["en_aliases"] = ValidationMath.GlobalShare(CorpusValidationExpectations.FullAliasEn, _ev.T1EnAliases).ToJson(),
            ["nb_aliases"] = ValidationMath.GlobalShare(CorpusValidationExpectations.FullAliasNb, _ev.T1NbAliases).ToJson(),
        };

        long zeroP31 = CorpusValidationExpectations.T1 - instance.DistinctSubjects;
        var p31Deg = new Dictionary<long, long>(instance.Degrees);
        p31Deg.TryGetValue(0, out long z0); p31Deg[0] = z0 + zeroP31;
        var p31Stats = ValidationMath.DegreeStats(p31Deg);

        long zeroP279T1 = CorpusValidationExpectations.T1 - subclass.DistinctT1Subjects;
        var p279Deg = new Dictionary<long, long>(subclass.T1Degrees);
        p279Deg.TryGetValue(0, out long pz); p279Deg[0] = pz + zeroP279T1;
        var p279Stats = ValidationMath.DegreeStats(p279Deg);

        return new Dictionary<string, object?>
        {
            ["itemFraction"] = new { n = frac.N, expected = Math.Round(frac.Expected, 3), difference = frac.Difference, fractionPercent = Math.Round(frac.Fraction * 100, 6), referenceBinomialZ = Math.Round(frac.Z, 3), note = "deterministic SHA-256 selection; binomial reference model is diagnostic only" },
            ["globalShares"] = shares,
            ["p31OutDegree"] = new { stats = StatsJson(p31Stats), fullSourceReference = new { median = 1, p90 = 1, p95 = 2, p99 = 2, max = 59 }, zeroDegreeCount = p31Deg[0] },
            ["p279T1OutDegree"] = new { stats = StatsJson(p279Stats), fullSourceReference = new { median = 0, p90 = 0, p95 = 0, p99 = 1, max = 26 }, note = "SubclassOf is complete, not sampled" },
            ["p31TargetFanout"] = FanoutJson(instance.TargetFanout),
        };
    }

    private static object? StatsJson((long Count, long Min, long Max, double Median, double P90, double P95, double P99, double Mean) s) => new
    {
        count = s.Count,
        min = s.Min,
        median = s.Median,
        p90 = s.P90,
        p95 = s.P95,
        p99 = s.P99,
        max = s.Max,
        mean = Math.Round(s.Mean, 4),
        quantileNote = "smallest d where cumulative count >= ceil(q*N)",
    };

    private object? FanoutJson(Dictionary<long, long> fanout)
    {
        var valueDist = new Dictionary<long, long>();
        foreach (var kv in fanout) { valueDist.TryGetValue(kv.Value, out long c); valueDist[kv.Value] = c + 1; }
        var targetStats = ValidationMath.DegreeStats(valueDist);

        string hintsPath = Path.Combine(_passA, "probe-hints.p31.jsonl");
        var probes = new List<object>();
        if (File.Exists(hintsPath))
        {
            foreach (var line in File.ReadLines(hintsPath))
            {
                using var doc = JsonDocument.Parse(line);
                long target = doc.RootElement.GetProperty("target").GetInt64();
                long full = doc.RootElement.GetProperty("fanout").GetInt64();
                long sampled = fanout.TryGetValue(target, out long v) ? v : 0;
                double expected = full * CorpusValidationExpectations.SampleFraction;
                probes.Add(new { target = $"Q{target}", full, sampled, expected = Math.Round(expected, 1), ratio = expected == 0 ? (double?)null : Math.Round(sampled / expected, 4) });
            }
        }
        return new { distinctTargets = fanout.Count, targetFanoutStats = new { count = targetStats.Count, min = targetStats.Min, median = targetStats.Median, p90 = targetStats.P90, p95 = targetStats.P95, p99 = targetStats.P99, max = targetStats.Max }, probes };
    }

    // ---------- anchors ----------
    private Dictionary<string, object?> ComputeAnchorCoverage(AnchorFixture anchors)
    {
        var result = new Dictionary<string, object?>();
        foreach (var set in anchors.Sets)
        {
            var qs = set.Qids.Select(q => long.Parse(q[1..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            long inT1 = 0, inT2 = 0, cap = 0, inCorpus = 0, absent = 0;
            foreach (long q in qs)
            {
                if (_concept.TryGet(q, out var e))
                {
                    inCorpus++;
                    if (e.InT1) inT1++;
                    if (e.InT2) inT2++;
                    if (e.InT1 && e.InT2) cap++;
                }
                else absent++;
            }
            result[set.Name] = new { total = qs.Count, inT1, inT2, inT1IntersectT2 = cap, inT2Only = inT2 - cap, inT1UnionT2 = inCorpus, absent };
        }
        return result;
    }

    private Dictionary<string, object?> ComputeLexicalSurface(AnchorFixture anchors, Dictionary<long, Dictionary<string, (HashSet<string> Labels, HashSet<string> Aliases)>> surfaces)
    {
        var byCategory = new Dictionary<string, List<object>>();
        var totals = new Dictionary<string, long> { ["concept_absent"] = 0, ["concept_present_surface_absent"] = 0, ["concept_present_label_surface"] = 0, ["concept_present_alias_surface"] = 0, ["not_applicable_to_baseline_lexical_corpus"] = 0 };

        foreach (var c in anchors.Cases)
        {
            if (c.Status != "resolved" || c.Qid == null) continue;
            long q = long.Parse(c.Qid[1..], System.Globalization.CultureInfo.InvariantCulture);
            string outcome;
            if (c.NameType is not ("CanonicalLabel" or "WikidataAlias"))
                outcome = "not_applicable_to_baseline_lexical_corpus";
            else if (!_concept.TryGet(q, out _))
                outcome = "concept_absent";
            else if (!surfaces.TryGetValue(q, out var perLang) || !perLang.TryGetValue(c.Language, out var entry))
                outcome = "concept_present_surface_absent";
            else
            {
                bool found = c.NameType == "CanonicalLabel" ? entry.Labels.Contains(c.Term) : entry.Aliases.Contains(c.Term);
                outcome = found
                    ? (c.NameType == "CanonicalLabel" ? "concept_present_label_surface" : "concept_present_alias_surface")
                    : "concept_present_surface_absent";
            }
            totals[outcome]++;
            if (!byCategory.TryGetValue(c.Category, out var list)) byCategory[c.Category] = list = new List<object>();
            list.Add(new { id = c.Id, language = c.Language, nameType = c.NameType, qid = c.Qid, outcome });
        }
        return new Dictionary<string, object?> { ["totals"] = totals, ["byCategory"] = byCategory };
    }

    private Dictionary<string, object?> ComputeAmbiguousContinuity(AnchorFixture anchors, Dictionary<long, Dictionary<string, (HashSet<string> Labels, HashSet<string> Aliases)>> surfaces)
    {
        var rows = new List<object>();
        foreach (var c in anchors.Cases.Where(c => c.Status == "ambiguous" && c.CandidateQids != null))
        {
            var cands = c.CandidateQids!.Select(x => long.Parse(x[1..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            long present = cands.Count(q => _concept.TryGet(q, out _));
            long withSurface = cands.Count(q =>
                surfaces.TryGetValue(q, out var perLang) && perLang.TryGetValue(c.Language, out var entry) &&
                (entry.Labels.Contains(c.Term) || entry.Aliases.Contains(c.Term)));
            rows.Add(new { id = c.Id, candidatesTotal = cands.Count, candidatesPresent = present, candidatesWithSurface = withSurface });
        }
        return new Dictionary<string, object?> { ["supportedCases"] = rows.Count, ["rows"] = rows };
    }

    // ---------- row-group streaming helpers ----------
    private static void ForEachLongPairs(string path, Action<long[], long[]> action)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var a = new long[n];
                var b = new long[n];
                Sync.Await(rg.ReadAsync<long>(f0, new Memory<long>(a), repetitionLevels: null, cancellationToken: default));
                Sync.Await(rg.ReadAsync<long>(f1, new Memory<long>(b), repetitionLevels: null, cancellationToken: default));
                action(a, b);
            }
        }
        finally
        {
            Sync.Await(reader.DisposeAsync());
        }
    }

    private sealed class LexicalGroup
    {
        public required long[] Qids;
        public required string[] Lang;
        public required string[] Kind;
        public required string[] Value;
    }

    private void ForEachLexicalGroup(Action<LexicalGroup> action)
    {
        string path = Path.Combine(_passB, "lexical_entry.parquet");
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            var f2 = (DataField)reader.Schema.DataFields[2];
            var f3 = (DataField)reader.Schema.DataFields[3];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var q = new long[n];
                var lang = new string[n];
                var kind = new string[n];
                var value = new string[n];
                Sync.Await(rg.ReadAsync<long>(f0, new Memory<long>(q), repetitionLevels: null, cancellationToken: default));
                Sync.Await(rg.ReadAsync(f1, new Memory<string>(lang), repetitionLevels: null, cancellationToken: default));
                Sync.Await(rg.ReadAsync(f2, new Memory<string>(kind), repetitionLevels: null, cancellationToken: default));
                Sync.Await(rg.ReadAsync(f3, new Memory<string>(value), repetitionLevels: null, cancellationToken: default));
                action(new LexicalGroup { Qids = q, Lang = lang, Kind = kind, Value = value });
            }
        }
        finally
        {
            Sync.Await(reader.DisposeAsync());
        }
    }

    private void Require(long expected, long actual, string label)
    {
        if (actual != expected) _ev.Fail($"{label}: expected {expected}, actual {actual}");
    }
}

public static class CorpusValidation
{
    public const string GO = "GO";
    public const string HOLD = "HOLD";

    public static string Run(string corpusRoot, string fixturePath, out CorpusValidationEvidence evidence)
    {
        var runner = new CorpusValidationRunner(corpusRoot, fixturePath);
        string verdict = runner.Run();
        evidence = runner.Evidence;
        return verdict;
    }
}
