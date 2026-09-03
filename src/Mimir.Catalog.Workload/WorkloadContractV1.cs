using System.Text.Json;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Parsed, validated representation of the tracked machine-readable workload
/// contract (benchmarks/workload-contract-v1.json). The JSON is the executable
/// source of truth for authoritative generation; this type is its strict parsed
/// view and carries the canonical semantic encoding used for workload identity.
/// Version values are schema-version markers and remain 1 until a published
/// contract actually changes.
/// </summary>
public sealed class WorkloadContractV1
{
    public const string Schema = "mimir-catalog-workload-contract-v1";

    public const int ContractVersion = 1;
    public const int GeneratorVersion = 1;
    public const int CanonicalEncodingVersion = Canon.CanonicalEncodingVersion;
    public const int MultisetFoldVersion = MultisetFoldV1.Version;

    public const string SelectionModeSample = "sha256-ranked-sample";
    public const string SelectionModeAll = "all-eligible";
    public const string SelectionModeMiss = "generated-miss";
    public const string SelectionModeDegreeRank = "degree-rank-500";

    public int WorkloadContractVersion { get; init; } = ContractVersion;
    public int GenVersion { get; init; } = GeneratorVersion;
    public int EncVersion { get; init; } = CanonicalEncodingVersion;
    public int FoldVersion { get; init; } = MultisetFoldVersion;

    public string SelectionDomain { get; init; } = "mimir-catalog-workload-v1";
    public string ConceptMissDomain { get; init; } = "mimir-catalog-workload-v1-miss-concept";
    public string LexicalMissDomain { get; init; } = "mimir-catalog-workload-v1-miss-lexical";
    public string MissLanguage { get; init; } = "nb";
    public string LexicalKeySemantics { get; init; } = "raw-exact-lang-value";

    public string OrderingAlgorithm { get; init; } = "sha256-rank";
    public string OrderingInterleave { get; init; } = "round-robin-stratum-order";
    public string PercentileAlgorithm { get; init; } = "linear-interpolated-n-minus-1-v1";
    public bool ReportP999 { get; init; } = false;

    public bool FullPassWarmup { get; init; } = true;
    public int ServingRepetitions { get; init; } = 3;
    public int OpenRepetitions { get; init; } = 5;
    public int AnalyticalRepetitions { get; init; } = 3;
    public int BuildRepetitions { get; init; } = 3;

    public int TimeoutPointOperationSeconds { get; init; } = 5;
    public int TimeoutG1StartSeconds { get; init; } = 30;
    public int TimeoutG2BatchSeconds { get; init; } = 120;
    public int TimeoutAnalyticalSeconds { get; init; } = 900;
    public int TimeoutBuildSeconds { get; init; } = 3600;

    public int MaxDepth { get; init; } = 3;
    public long VisitedNodeGuard { get; init; } = 5_000;
    public int G2BatchConcepts { get; init; } = 200;

    public IReadOnlyList<StratumDef> Strata { get; init; } = DefaultStrata();
    public IReadOnlyList<StratumDef> CorrectnessOnly { get; init; } = DefaultCorrectnessOnly();
    public IReadOnlyList<G2Def> G2Strata { get; init; } = DefaultG2();

    public sealed record StratumDef(
        string Operation,
        string Stratum,
        string SelectionMode,
        long MeasuredCount,
        long? FanoutMin = null,
        long? FanoutMax = null,
        long? ExpectedEligibleCount = null);

    public sealed record G2Def(string Stratum, long Count);

    /// <summary>Convenience default mirroring the tracked contract; never the authoritative source.</summary>
    public static WorkloadContractV1 Default() => new();

    public static List<StratumDef> DefaultStrata() =>
    [
        new("S1", "T1Only", SelectionModeSample, 4000),
        new("S1", "T2Only", SelectionModeSample, 4000),
        new("S1", "T1IntersectT2", SelectionModeSample, 4000),
        new("S1", "Absent", SelectionModeMiss, 4000),
        new("S2", "Fanout1", SelectionModeSample, 4000, 1, 1),
        new("S2", "Fanout2To5", SelectionModeSample, 4000, 2, 5),
        new("S2", "Fanout6To50", SelectionModeSample, 2000, 6, 50),
        new("S2", "Fanout51Plus", SelectionModeAll, 380, 51, null, 380),
        new("S2", "Miss", SelectionModeMiss, 4000),
        new("S3", "T1WithLexical", SelectionModeSample, 1500),
        new("S3", "T2OnlyWithLexical", SelectionModeSample, 1500),
        new("S3", "ConceptNoLexical", SelectionModeSample, 1000),
        new("S4", "Degree0", SelectionModeSample, 3000),
        new("S4", "Degree1", SelectionModeSample, 3000),
        new("S4", "Degree2Plus", SelectionModeSample, 3000),
        new("S4", "HighDegree", SelectionModeDegreeRank, 500),
        new("S5", "Degree0", SelectionModeSample, 3000),
        new("S5", "Degree1", SelectionModeSample, 3000),
        new("S5", "Degree2Plus", SelectionModeSample, 3000),
        new("G1", "Degree1", SelectionModeSample, 250),
        new("G1", "Degree2Plus", SelectionModeSample, 250),
    ];

    public static List<StratumDef> DefaultCorrectnessOnly() =>
    [
        new("S1", "Tail", SelectionModeAll, 20, null, null, 20),
    ];

    public static List<G2Def> DefaultG2() =>
    [
        new("P31Degree1", 100),
        new("P31Degree2Plus", 100),
    ];

    /// <summary>The eight frozen analytical operations.</summary>
    public static readonly string[] AnalyticalOperations =
    [
        "A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf",
        "A2", "A3", "A4", "A5",
    ];

    // ---- strict JSON parsing ----

    private static readonly HashSet<string> KnownTop = new(StringComparer.Ordinal)
    {
        "schema", "workloadContractVersion", "generatorVersion", "canonicalEncodingVersion",
        "multisetFoldVersion", "selectionDomain", "conceptMissDomain", "lexicalMissDomain",
        "missLanguage", "lexicalKeySemantics", "orderingAlgorithm", "orderingInterleave",
        "percentileAlgorithm", "reportP999", "fullPassWarmup", "servingRepetitions",
        "openRepetitions", "analyticalRepetitions", "buildRepetitions",
        "timeoutPointOperationSeconds", "timeoutG1StartSeconds", "timeoutG2BatchSeconds",
        "timeoutAnalyticalSeconds", "timeoutBuildSeconds", "maxDepth", "visitedNodeGuard",
        "g2BatchConcepts", "strata", "correctnessOnly", "g2Strata",
    };

    private static readonly HashSet<string> KnownStratum = new(StringComparer.Ordinal)
    {
        "operation", "stratum", "selectionMode", "measuredCount", "fanoutMin", "fanoutMax", "expectedEligibleCount",
    };

    public static WorkloadContractV1 Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("workload contract root must be an object");

        foreach (var p in r.EnumerateObject())
            if (!KnownTop.Contains(p.Name))
                throw new InvalidDataException($"unknown workload contract field: {p.Name}");

        string schema = r.GetProperty("schema").GetString() ?? string.Empty;
        if (schema != Schema) throw new InvalidDataException($"schema mismatch: {schema}");

        int I(string n)
        {
            if (!r.TryGetProperty(n, out var v)) throw new InvalidDataException($"missing required field {n}");
            return v.GetInt32();
        }
        long L(string n)
        {
            if (!r.TryGetProperty(n, out var v)) throw new InvalidDataException($"missing required field {n}");
            return v.GetInt64();
        }
        bool B(string n)
        {
            if (!r.TryGetProperty(n, out var v)) throw new InvalidDataException($"missing required field {n}");
            return v.GetBoolean();
        }
        string S(string n)
        {
            if (!r.TryGetProperty(n, out var v)) throw new InvalidDataException($"missing required field {n}");
            return v.GetString() ?? throw new InvalidDataException($"non-string field {n}");
        }

        if (I("workloadContractVersion") != ContractVersion)
            throw new InvalidDataException("unsupported workloadContractVersion");
        if (I("generatorVersion") != GeneratorVersion)
            throw new InvalidDataException("unsupported generatorVersion");
        if (I("canonicalEncodingVersion") != CanonicalEncodingVersion)
            throw new InvalidDataException("unsupported canonicalEncodingVersion");
        if (I("multisetFoldVersion") != MultisetFoldVersion)
            throw new InvalidDataException("unsupported multisetFoldVersion");

        var contract = new WorkloadContractV1
        {
            WorkloadContractVersion = ContractVersion,
            GenVersion = GeneratorVersion,
            EncVersion = CanonicalEncodingVersion,
            FoldVersion = MultisetFoldVersion,
            SelectionDomain = S("selectionDomain"),
            ConceptMissDomain = S("conceptMissDomain"),
            LexicalMissDomain = S("lexicalMissDomain"),
            MissLanguage = S("missLanguage"),
            LexicalKeySemantics = S("lexicalKeySemantics"),
            OrderingAlgorithm = S("orderingAlgorithm"),
            OrderingInterleave = S("orderingInterleave"),
            PercentileAlgorithm = S("percentileAlgorithm"),
            ReportP999 = B("reportP999"),
            FullPassWarmup = B("fullPassWarmup"),
            ServingRepetitions = I("servingRepetitions"),
            OpenRepetitions = I("openRepetitions"),
            AnalyticalRepetitions = I("analyticalRepetitions"),
            BuildRepetitions = I("buildRepetitions"),
            TimeoutPointOperationSeconds = I("timeoutPointOperationSeconds"),
            TimeoutG1StartSeconds = I("timeoutG1StartSeconds"),
            TimeoutG2BatchSeconds = I("timeoutG2BatchSeconds"),
            TimeoutAnalyticalSeconds = I("timeoutAnalyticalSeconds"),
            TimeoutBuildSeconds = I("timeoutBuildSeconds"),
            MaxDepth = I("maxDepth"),
            VisitedNodeGuard = L("visitedNodeGuard"),
            G2BatchConcepts = I("g2BatchConcepts"),
            Strata = ParseStrata(r.GetProperty("strata")),
            CorrectnessOnly = ParseStrata(r.GetProperty("correctnessOnly")),
            G2Strata = ParseG2(r.GetProperty("g2Strata")),
        };

        Validate(contract);
        return contract;
    }

    private static List<StratumDef> ParseStrata(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("strata must be an array");
        var list = new List<StratumDef>();
        foreach (var el in arr.EnumerateArray())
        {
            foreach (var p in el.EnumerateObject())
                if (!KnownStratum.Contains(p.Name))
                    throw new InvalidDataException($"unknown stratum field: {p.Name}");
            string operation = el.GetProperty("operation").GetString() ?? throw new InvalidDataException("missing stratum operation");
            string stratum = el.GetProperty("stratum").GetString() ?? throw new InvalidDataException("missing stratum name");
            string mode = el.GetProperty("selectionMode").GetString() ?? throw new InvalidDataException("missing selectionMode");
            long count = el.GetProperty("measuredCount").GetInt64();
            long? fanoutMin = el.TryGetProperty("fanoutMin", out var fmin) && fmin.ValueKind != JsonValueKind.Null ? fmin.GetInt64() : null;
            long? fanoutMax = el.TryGetProperty("fanoutMax", out var fmax) && fmax.ValueKind != JsonValueKind.Null ? fmax.GetInt64() : null;
            long? expected = el.TryGetProperty("expectedEligibleCount", out var exp) && exp.ValueKind != JsonValueKind.Null ? exp.GetInt64() : null;
            list.Add(new StratumDef(operation, stratum, mode, count, fanoutMin, fanoutMax, expected));
        }
        return list;
    }

    private static readonly HashSet<string> KnownG2 = new(StringComparer.Ordinal) { "stratum", "count" };

    private static List<G2Def> ParseG2(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("g2Strata must be an array");
        var list = new List<G2Def>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) throw new InvalidDataException("g2 stratum must be an object");
            foreach (var p in el.EnumerateObject())
                if (!KnownG2.Contains(p.Name))
                    throw new InvalidDataException($"unknown G2 field: {p.Name}");
            string stratum = el.GetProperty("stratum").GetString() ?? throw new InvalidDataException("missing g2 stratum");
            long count = el.GetProperty("count").GetInt64();
            list.Add(new G2Def(stratum, count));
        }
        return list;
    }

    public static void Validate(WorkloadContractV1 c)
    {
        var seen = new HashSet<(string Op, string Stratum)>();
        foreach (var s in c.Strata.Concat(c.CorrectnessOnly))
        {
            if (!seen.Add((s.Operation, s.Stratum)))
                throw new InvalidDataException($"duplicate operation/stratum: {s.Operation}/{s.Stratum}");
            switch (s.SelectionMode)
            {
                case SelectionModeSample:
                    if (s.FanoutMin == null && s.FanoutMax == null && s.ExpectedEligibleCount != null)
                        throw new InvalidDataException("sampled stratum must not carry expectedEligibleCount");
                    break;
                case SelectionModeAll:
                    if (s.ExpectedEligibleCount == null || s.ExpectedEligibleCount != s.MeasuredCount)
                        throw new InvalidDataException($"all-eligible stratum requires expectedEligibleCount == measuredCount ({s.Operation}/{s.Stratum})");
                    break;
                case SelectionModeMiss:
                case SelectionModeDegreeRank:
                    break;
                default:
                    throw new InvalidDataException($"unsupported selectionMode {s.SelectionMode} on {s.Operation}/{s.Stratum}");
            }
            if (s.MeasuredCount < 0) throw new InvalidDataException("negative measuredCount");
            if (s.FanoutMin != null && s.FanoutMax != null && s.FanoutMin > s.FanoutMax)
                throw new InvalidDataException($"fanout range inverted on {s.Operation}/{s.Stratum}");
            if (s.Operation == "S2" && s.SelectionMode != SelectionModeMiss && s.FanoutMin == null)
                throw new InvalidDataException($"S2 hit stratum {s.Stratum} must declare fanoutMin");
        }

        // S2 hit ranges must form one contiguous partition 1..unbounded:
        // no overlap, no gaps, first min = 1, only the final range is unbounded
        // and the final range must be unbounded.
        var ranges = c.Strata.Where(s => s.Operation == "S2" && s.SelectionMode != SelectionModeMiss)
            .Select(s => (s.Stratum, s.FanoutMin!.Value, s.FanoutMax)).OrderBy(x => x.Item2).ToList();
        if (ranges.Count == 0 || ranges[0].Item2 != 1)
            throw new InvalidDataException("S2 hit ranges must start at min 1");
        long? prevMax = null;
        for (int i = 0; i < ranges.Count; i++)
        {
            var (name, min, max) = ranges[i];
            bool finalRange = i == ranges.Count - 1;
            if (prevMax != null && min != prevMax + 1)
                throw new InvalidDataException($"S2 fanout ranges gap before {name}");
            if (prevMax != null && min <= prevMax)
                throw new InvalidDataException($"S2 fanout ranges overlap at {name}");
            if (max == null && !finalRange)
                throw new InvalidDataException($"non-final S2 range must be bounded: {name}");
            if (finalRange && max != null)
                throw new InvalidDataException($"final S2 range must be unbounded: {name}");
            prevMax = max;
        }

        long g2Total = c.G2Strata.Sum(g => g.Count);
        if (g2Total != c.G2BatchConcepts)
            throw new InvalidDataException("g2Strata counts must sum to g2BatchConcepts");
        if (c.G2Strata.Select(g => g.Stratum).Distinct().Count() != c.G2Strata.Count)
            throw new InvalidDataException("duplicate G2 stratum");
        if (c.MaxDepth < 0 || c.VisitedNodeGuard <= 0 || c.G2BatchConcepts <= 0)
            throw new InvalidDataException("invalid graph parameters");
        if (c.TimeoutPointOperationSeconds <= 0 || c.TimeoutG1StartSeconds <= 0 || c.TimeoutG2BatchSeconds <= 0 ||
            c.TimeoutAnalyticalSeconds <= 0 || c.TimeoutBuildSeconds <= 0)
            throw new InvalidDataException("timeouts must be positive");
        if (c.PercentileAlgorithm != "linear-interpolated-n-minus-1-v1")
            throw new InvalidDataException($"unsupported percentileAlgorithm: {c.PercentileAlgorithm}");
        if (c.ReportP999) throw new InvalidDataException("reportP999 must be false in v1");

        // Semantic gates: reject machine-contract values the generator does not implement.
        if (c.LexicalKeySemantics != "raw-exact-lang-value")
            throw new InvalidDataException($"unsupported lexicalKeySemantics: {c.LexicalKeySemantics}");
        if (c.OrderingAlgorithm != "sha256-rank")
            throw new InvalidDataException($"unsupported orderingAlgorithm: {c.OrderingAlgorithm}");
        if (c.OrderingInterleave != "round-robin-stratum-order")
            throw new InvalidDataException($"unsupported orderingInterleave: {c.OrderingInterleave}");
        if (!c.FullPassWarmup)
            throw new InvalidDataException("v1 only supports fullPassWarmup=true");

        // Operation/stratum vocabulary.
        var allowed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["S1"] = new(StringComparer.Ordinal) { "T1Only", "T2Only", "T1IntersectT2", "Absent" },
            ["S2"] = new(StringComparer.Ordinal) { "Fanout1", "Fanout2To5", "Fanout6To50", "Fanout51Plus", "Miss" },
            ["S3"] = new(StringComparer.Ordinal) { "T1WithLexical", "T2OnlyWithLexical", "ConceptNoLexical" },
            ["S4"] = new(StringComparer.Ordinal) { "Degree0", "Degree1", "Degree2Plus", "HighDegree" },
            ["S5"] = new(StringComparer.Ordinal) { "Degree0", "Degree1", "Degree2Plus" },
            ["G1"] = new(StringComparer.Ordinal) { "Degree1", "Degree2Plus" },
        };
        foreach (var s in c.Strata)
        {
            if (!allowed.TryGetValue(s.Operation, out var names) || !names.Contains(s.Stratum))
                throw new InvalidDataException($"unsupported operation/stratum: {s.Operation}/{s.Stratum}");
        }

        // Require the complete v1 stratum vocabulary and its supported
        // per-stratum selectionMode semantics. Counts stay executable values.
        var required = new (string Op, string Stratum, string Mode)[]
        {
            ("S1", "T1Only", SelectionModeSample),
            ("S1", "T2Only", SelectionModeSample),
            ("S1", "T1IntersectT2", SelectionModeSample),
            ("S1", "Absent", SelectionModeMiss),
            ("S2", "Fanout1", SelectionModeSample),
            ("S2", "Fanout2To5", SelectionModeSample),
            ("S2", "Fanout6To50", SelectionModeSample),
            ("S2", "Fanout51Plus", SelectionModeAll),
            ("S2", "Miss", SelectionModeMiss),
            ("S3", "T1WithLexical", SelectionModeSample),
            ("S3", "T2OnlyWithLexical", SelectionModeSample),
            ("S3", "ConceptNoLexical", SelectionModeSample),
            ("S4", "Degree0", SelectionModeSample),
            ("S4", "Degree1", SelectionModeSample),
            ("S4", "Degree2Plus", SelectionModeSample),
            ("S4", "HighDegree", SelectionModeDegreeRank),
            ("S5", "Degree0", SelectionModeSample),
            ("S5", "Degree1", SelectionModeSample),
            ("S5", "Degree2Plus", SelectionModeSample),
            ("G1", "Degree1", SelectionModeSample),
            ("G1", "Degree2Plus", SelectionModeSample),
        };
        foreach (var (op, stratum, mode) in required)
        {
            var matches = c.Strata.Where(s => s.Operation == op && s.Stratum == stratum).ToList();
            if (matches.Count != 1)
                throw new InvalidDataException($"required stratum missing or duplicated: {op}/{stratum}");
            if (matches[0].SelectionMode != mode)
                throw new InvalidDataException($"unsupported selectionMode for {op}/{stratum}: {matches[0].SelectionMode}");
        }

        // Census invariants (frozen v1 semantics).
        var fanout51 = c.Strata.Single(s => s.Operation == "S2" && s.Stratum == "Fanout51Plus");
        if (fanout51.FanoutMin != 51 || fanout51.FanoutMax != null ||
            fanout51.MeasuredCount != 380 || fanout51.ExpectedEligibleCount != 380)
            throw new InvalidDataException("Fanout51Plus census must be fanoutMin 51, unbounded, measured 380, expected 380");

        // Correctness-only vocabulary: exactly S1/Tail.
        if (c.CorrectnessOnly.Count != 1)
            throw new InvalidDataException("v1 requires exactly one correctness-only definition: S1/Tail");
        var tail = c.CorrectnessOnly[0];
        if (tail.Operation != "S1" || tail.Stratum != "Tail")
            throw new InvalidDataException("v1 correctness-only must be S1/Tail");
        if (tail.SelectionMode != SelectionModeAll)
            throw new InvalidDataException("S1/Tail correctness-only must use all-eligible");
        if (tail.ExpectedEligibleCount == null || tail.ExpectedEligibleCount != tail.MeasuredCount)
            throw new InvalidDataException("S1/Tail correctness-only requires expectedEligibleCount == measuredCount");
        if (tail.MeasuredCount != 20 || tail.ExpectedEligibleCount != 20)
            throw new InvalidDataException("S1/Tail correctness-only must be 20/20 (all-eligible census)");

        // G2 vocabulary: exactly P31Degree1 and P31Degree2Plus.
        var g2 = c.G2Strata.Select(g => g.Stratum).ToHashSet(StringComparer.Ordinal);
        if (g2.Count != 2 || !g2.Contains("P31Degree1") || !g2.Contains("P31Degree2Plus"))
            throw new InvalidDataException("v1 requires exactly the G2 strata P31Degree1 and P31Degree2Plus");
        if (c.G2Strata.Single(g => g.Stratum == "P31Degree1").Count != 100 ||
            c.G2Strata.Single(g => g.Stratum == "P31Degree2Plus").Count != 100)
            throw new InvalidDataException("v1 G2 strata counts must each be 100");
    }

    /// <summary>
    /// Canonical semantic image of every normative executable contract value.
    /// This is what workload identity hashes; no JSON whitespace/order or
    /// operational data is included.
    /// </summary>
    public byte[] CanonicalNormative()
    {
        var b = new Canon.Builder();
        b.AddString(Schema);
        b.AddLong(WorkloadContractVersion).AddLong(GenVersion).AddLong(EncVersion).AddLong(FoldVersion);
        b.AddString(SelectionDomain).AddString(ConceptMissDomain).AddString(LexicalMissDomain);
        b.AddString(MissLanguage).AddString(LexicalKeySemantics);
        b.AddString(OrderingAlgorithm).AddString(OrderingInterleave).AddString(PercentileAlgorithm);
        b.AddByte(ReportP999 ? (byte)1 : (byte)0).AddByte(FullPassWarmup ? (byte)1 : (byte)0);
        b.AddLong(ServingRepetitions).AddLong(OpenRepetitions).AddLong(AnalyticalRepetitions).AddLong(BuildRepetitions);
        b.AddLong(TimeoutPointOperationSeconds).AddLong(TimeoutG1StartSeconds).AddLong(TimeoutG2BatchSeconds);
        b.AddLong(TimeoutAnalyticalSeconds).AddLong(TimeoutBuildSeconds);
        b.AddLong(MaxDepth).AddLong(VisitedNodeGuard).AddLong(G2BatchConcepts);
        foreach (var s in Strata) { b.AddString(s.Operation).AddString(s.Stratum).AddString(s.SelectionMode).AddLong(s.MeasuredCount); b.AddLong(s.FanoutMin ?? -1).AddLong(s.FanoutMax ?? -1).AddLong(s.ExpectedEligibleCount ?? -1); }
        foreach (var s in CorrectnessOnly) { b.AddString(s.Operation).AddString(s.Stratum).AddString(s.SelectionMode).AddLong(s.MeasuredCount); b.AddLong(s.FanoutMin ?? -1).AddLong(s.FanoutMax ?? -1).AddLong(s.ExpectedEligibleCount ?? -1); }
        foreach (var g in G2Strata) b.AddString(g.Stratum).AddLong(g.Count);
        return b.ToArray();
    }
}
