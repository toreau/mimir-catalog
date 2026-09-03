using System.Text.Json;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Deterministic workload builder. Given the neutral reference tables (loaded
/// from the accepted canonical corpus or a synthetic model), the parsed tracked
/// contract and a lexical row source, it proves pool sufficiency (including the
/// all-eligible Fanout51Plus census), then produces the authoritative
/// probe/expected artifacts.
/// </summary>
public static class WorkloadBuild
{
    public const string Go = "GO";
    public const string Hold = "HOLD";

    public sealed class Result
    {
        public required string Verdict { get; set; }
        public List<string> Reasons { get; } = new();
        public Dictionary<string, long> PoolCardinalities { get; } = new();
        public string? WorkloadId { get; set; }
        public byte[]? ServingLines { get; set; }
        public byte[]? GraphLines { get; set; }
        public byte[]? ExpectedLines { get; set; }
        public byte[]? AnalyticalLines { get; set; }
        public Dictionary<string, object> Continuity { get; } = new();
        public long MeasuredServingCount { get; set; }
        public long MeasuredG1Count { get; set; }
        public int G2BatchCount { get; set; }
        public long LexicalRowCount { get; set; }
        public long InstanceRowCount { get; set; }
        public long SubclassRowCount { get; set; }
        public int G1CandidatesConsidered { get; set; }
        public int G1RejectedGuard { get; set; }
        public long G1MaxVisited { get; set; }
        public int G2CandidatesConsidered { get; set; }
        public int G2RejectedGuard { get; set; }
        public int G2Accepted { get; set; }
        public long G2MaxVisited { get; set; }
    }

    private readonly record struct PlanItem(
        string Op, string Stratum, bool Measured, long Seq, long Qid, string? Lang, string? Value);

    public static Result Build(
        WorkloadContractV1 c,
        string corpusId,
        ConceptTable concept,
        LexicalStats lexical,
        EdgeTable instance,
        EdgeTable subclass,
        Func<IEnumerable<ParquetLoader.LexRow>> lexRows,
        string fixturePath)
    {
        var result = new Result { Verdict = Go };
        var domain = c.SelectionDomain;
        var tailSet = new HashSet<long>(concept.TailQids);

        // ---- concept tier / lexical-presence pools ----
        var t1OnlyPool = new List<long>();
        var t2OnlyPool = new List<long>();
        var capPool = new List<long>();
        var t1WithLex = new List<long>();
        var t2OnlyWithLex = new List<long>();
        var noLex = new List<long>();
        for (int i = 0; i < concept.Count; i++)
        {
            bool in1 = (concept.Flags[i] & 1) != 0;
            bool in2 = (concept.Flags[i] & 2) != 0;
            long q = concept.Qids[i];
            bool hasLex = lexical.WithLexical.Contains(q);
            if (in1 && in2) capPool.Add(q);
            else if (!in1 && in2 && !tailSet.Contains(q)) t2OnlyPool.Add(q);
            else if (in1 && !in2) t1OnlyPool.Add(q);
            if (in1) { if (hasLex) t1WithLex.Add(q); }
            else if (in2 && !tailSet.Contains(q)) { if (hasLex) t2OnlyWithLex.Add(q); }
            if (!hasLex && !tailSet.Contains(q)) noLex.Add(q); // tail is correctness-only
        }

        // ---- P31 (InstanceOf) degree buckets over T1 concepts ----
        var instByDegree = new Dictionary<int, List<long>>();
        for (int i = 0; i < concept.Count; i++)
        {
            if ((concept.Flags[i] & 1) == 0) continue;
            long q = concept.Qids[i];
            int d = instance.DegreeOf(q) ?? 0;
            if (!instByDegree.TryGetValue(d, out var b)) { b = new List<long>(); instByDegree[d] = b; }
            b.Add(q);
        }
        var instDeg2PlusAll = instByDegree.Where(k => k.Key >= 2).SelectMany(k => k.Value).ToList();

        // High-degree 500: degree desc, then hash asc (HighDegree), then QID asc.
        var instHigh = new List<long>();
        foreach (var kv in instByDegree.OrderByDescending(k => k.Key))
        {
            if (kv.Key < 1) continue;
            int need = (int)Math.Min(500 - instHigh.Count, kv.Value.Count);
            if (need <= 0) break;
            var ranked = WorkloadSelection.RankTopQids(kv.Value, domain, "S4", "HighDegree", need);
            instHigh.AddRange(ranked.Select(r => r.Qid));
            if (instHigh.Count >= 500) break;
        }
        var instHighSet = new HashSet<long>(instHigh);
        var instDeg2PlusOrdinary = instDeg2PlusAll.Where(q => !instHighSet.Contains(q)).ToList();
        var instDeg0 = instByDegree.TryGetValue(0, out var d0) ? d0 : new List<long>();
        var instDeg1 = instByDegree.TryGetValue(1, out var d1) ? d1 : new List<long>();

        // ---- P279 (SubclassOf) degree pools over concept subjects ----
        var subjDeg0 = new List<long>();
        var subjDeg1 = new List<long>();
        var subjDeg2 = new List<long>();
        foreach (long s in subclass.Subjects)
        {
            if (!concept.TryGet(s, out _, out _)) continue;
            int d = (subclass.DegreeOf(s) ?? 0);
            if (d == 0) subjDeg0.Add(s);
            else if (d == 1) subjDeg1.Add(s);
            else subjDeg2.Add(s);
        }
        var subjDeg0Concepts = new List<long>();
        for (int i = 0; i < concept.Count; i++)
        {
            long q = concept.Qids[i];
            if (subclass.DegreeOf(q) == null) subjDeg0Concepts.Add(q);
        }

        var qidPools = new Dictionary<string, List<long>>
        {
            ["S1/T1Only"] = t1OnlyPool,
            ["S1/T2Only"] = t2OnlyPool,
            ["S1/T1IntersectT2"] = capPool,
            ["S3/T1WithLexical"] = t1WithLex,
            ["S3/T2OnlyWithLexical"] = t2OnlyWithLex,
            ["S3/ConceptNoLexical"] = noLex,
            ["S4/Degree0"] = instDeg0,
            ["S4/Degree1"] = instDeg1,
            ["S4/Degree2Plus"] = instDeg2PlusOrdinary,
            ["S4/HighDegree"] = instHigh,
            ["S5/Degree0"] = subjDeg0Concepts,
            ["S5/Degree1"] = subjDeg1,
            ["S5/Degree2Plus"] = subjDeg2,
            ["G1/Degree1"] = subjDeg1,
            ["G1/Degree2Plus"] = subjDeg2,
            ["G2/P31Degree1"] = instDeg1,
            ["G2/P31Degree2Plus"] = instDeg2PlusAll, // all T1 with P31 degree >= 2 (high-degree included)
        };
        foreach (var kv in qidPools) result.PoolCardinalities[kv.Key] = kv.Value.Count;

        // ---- lexical fanout pools from explicit contract ranges ----
        var lexPools = new Dictionary<string, List<(string, string)>>();
        foreach (var st in c.Strata.Where(s => s.Operation == "S2" && s.SelectionMode != WorkloadContractV1.SelectionModeMiss))
        {
            var list = new List<(string, string)>();
            foreach (var kv in lexical.Fanout)
            {
                if (kv.Value < st.FanoutMin) continue;
                if (st.FanoutMax != null && kv.Value > st.FanoutMax) continue;
                list.Add(kv.Key);
            }
            lexPools[$"S2/{st.Stratum}"] = list;
            result.PoolCardinalities[$"S2/{st.Stratum}"] = list.Count;
        }

        // ---- sufficiency / census gates ----
        foreach (var st in c.Strata)
        {
            bool generated = (st.Operation == "S1" && st.Stratum == "Absent") || (st.Operation == "S2" && st.Stratum == "Miss");
            if (generated) continue;
            string key = $"{st.Operation}/{st.Stratum}";
            if (st.Operation == "S2")
            {
                var pool = lexPools[key];
                if (st.SelectionMode == WorkloadContractV1.SelectionModeAll)
                {
                    if (pool.Count != st.ExpectedEligibleCount)
                    {
                        result.Verdict = Hold;
                        result.Reasons.Add($"{key}: census mismatch, eligible {pool.Count}, expected {st.ExpectedEligibleCount}");
                    }
                }
                else if (pool.Count < st.MeasuredCount)
                {
                    result.Verdict = Hold;
                    result.Reasons.Add($"{key}: required measured {st.MeasuredCount}, eligible pool {pool.Count}");
                }
                continue;
            }
            long have = qidPools.TryGetValue(key, out var qp) ? qp.Count : 0;
            if (have < st.MeasuredCount)
            {
                result.Verdict = Hold;
                result.Reasons.Add($"{key}: required measured {st.MeasuredCount}, eligible pool {have}");
            }
        }

        // Continuity subgroup (diagnostic; never latency-weighted).
        result.Continuity["resolvedGoldPresent"] = CountPresent(concept, LoadSet(fixturePath, "resolvedGold"));
        result.Continuity["goldUnionPresent"] = CountPresent(concept, LoadSet(fixturePath, "goldUnion"));
        result.Continuity["ambiguousCandPresent"] = CountPresent(concept, LoadSet(fixturePath, "ambiguousCand"));
        result.Continuity["lexicalSurfacePresent"] = CountLexicalSurfaces(fixturePath, lexical);
        result.Continuity["ambiguousMultiPresent"] = CountAmbiguousMulti(fixturePath, concept);

        if (result.Verdict == Hold) return result;

        // ---- serving probe plan (measured, then correctness tail) ----
        var servingPlan = new List<PlanItem>();
        foreach (var op in new[] { "S1", "S2", "S3", "S4", "S5" })
        {
            var defs = c.Strata.Where(s => s.Operation == op).ToList();
            var arrays = new List<PlanItem[]>();
            foreach (var st in defs)
            {
                PlanItem[] sel;
                if (op == "S1" && st.Stratum == "Absent")
                {
                    sel = WorkloadSelection.GenerateConceptMisses(
                            q => concept.TryGet(q, out _, out _), c.ConceptMissDomain, op, st.Stratum, (int)st.MeasuredCount)
                        .Select(m => new PlanItem(op, st.Stratum, true, 0, m.Qid, null, null)).ToArray();
                }
                else if (op == "S2" && st.Stratum == "Miss")
                {
                    sel = WorkloadSelection.GenerateLexicalMisses(
                            (lg, v) => lexical.Fanout.ContainsKey((lg, v)), c.LexicalMissDomain, op, st.Stratum, (int)st.MeasuredCount,
                            new[] { c.MissLanguage })
                        .Select(m => new PlanItem(op, st.Stratum, true, 0, 0, m.Lang, m.Value)).ToArray();
                }
                else if (op == "S2")
                {
                    var pool = lexPools[$"S2/{st.Stratum}"];
                    int n = st.SelectionMode == WorkloadContractV1.SelectionModeAll ? pool.Count : (int)st.MeasuredCount;
                    sel = WorkloadSelection.RankTopLex(pool, domain, op, st.Stratum, n)
                        .Select(m => new PlanItem(op, st.Stratum, true, 0, 0, m.Lang, m.Value)).ToArray();
                }
                else
                {
                    sel = WorkloadSelection.RankTopQids(qidPools[$"{op}/{st.Stratum}"], domain, op, st.Stratum, (int)st.MeasuredCount)
                        .Select(m => new PlanItem(op, st.Stratum, true, 0, m.Qid, null, null)).ToArray();
                }
                arrays.Add(sel);
            }
            var merged = WorkloadSelection.RoundRobinInterleave(arrays);
            foreach (var p in merged) servingPlan.Add(p);
        }
        long servingMeasured = servingPlan.Count;

        var tailDef = c.CorrectnessOnly.SingleOrDefault();
        if (tailDef == null || tailDef.Operation != "S1" || tailDef.Stratum != "Tail")
        {
            result.Verdict = Hold;
            result.Reasons.Add("correctness-only S1/Tail definition missing");
            return result;
        }
        if (concept.TailQids.Count != tailDef.ExpectedEligibleCount)
        {
            result.Verdict = Hold;
            result.Reasons.Add($"S1/Tail census mismatch: concept tail {concept.TailQids.Count}, contract expected {tailDef.ExpectedEligibleCount}");
            return result;
        }
        foreach (long tq in concept.TailQids)
            servingPlan.Add(new PlanItem("S1", "Tail", false, 0, tq, null, null));

        var servingBytes = new List<byte[]>();
        var servingFinal = new List<PlanItem>();
        for (int i = 0; i < servingPlan.Count; i++)
        {
            var p = servingPlan[i];
            var fp = p with { Seq = i };
            servingFinal.Add(fp);
            servingBytes.Add(ObjectLine(w =>
            {
                w.WriteString("op", fp.Op);
                w.WriteString("stratum", fp.Stratum);
                w.WriteNumber("seq", fp.Seq);
                w.WriteBoolean("measured", fp.Measured);
                if (fp.Lang != null) { w.WriteString("lang", fp.Lang); w.WriteString("value", fp.Value); }
                else w.WriteNumber("qid", fp.Qid);
            }));
        }

        // ---- G1 probe plan (guard-fitted, ranked) ----
        var graphPlan = new List<PlanItem>();
        int consideredG1 = 0, rejectedG1 = 0;
        long maxVisitedG1 = 0;
        foreach (var st in c.Strata.Where(s => s.Operation == "G1"))
        {
            string key = $"G1/{st.Stratum}";
            long found = 0;
            foreach (var it in WorkloadSelection.RankTopQids(qidPools[key], domain, "G1", st.Stratum, qidPools[key].Count))
            {
                consideredG1++;
                var trav = GraphTraversal.Ancestry(it.Qid, c.MaxDepth, c.VisitedNodeGuard, q => subclass.TryGetTargets(q, out var ts) ? ts : Array.Empty<long>());
                if (trav.ExceededGuard) { rejectedG1++; continue; }
                maxVisitedG1 = Math.Max(maxVisitedG1, trav.VisitedCount);
                graphPlan.Add(new PlanItem("G1", st.Stratum, true, 0, it.Qid, null, null));
                found++;
                if (found >= st.MeasuredCount) break;
            }
            if (found < st.MeasuredCount)
            {
                result.Verdict = Hold;
                result.Reasons.Add($"G1/{st.Stratum}: only {found} ranked starts fit the {c.VisitedNodeGuard}-node guard");
                return result;
            }
        }
        result.G1CandidatesConsidered = consideredG1;
        result.G1RejectedGuard = rejectedG1;
        result.G1MaxVisited = maxVisitedG1;
        long graphMeasured = graphPlan.Count;

        // ---- G2 batch plan (deg1 then deg>=2, ranked, guard-fitted) ----
        var specs = new List<(string Stratum, long Count, IReadOnlyList<long> Pool)>();
        foreach (var gd in c.G2Strata)
            specs.Add((gd.Stratum, gd.Count, gd.Stratum == "P31Degree2Plus" ? instDeg2PlusAll : instDeg1));

        var g2Sel = G2BatchSelection.Select(
            specs,
            q => instance.TryGetTargets(q, out var ts) ? ts : null,
            q => subclass.TryGetTargets(q, out var ts) ? ts : Array.Empty<long>(),
            domain, c.MaxDepth, c.VisitedNodeGuard);
        var g2Inputs = g2Sel.Inputs;

        if (g2Sel.Shortfalls.Count > 0)
        {
            result.Verdict = Hold;
            foreach (var sf in g2Sel.Shortfalls) result.Reasons.Add(sf);
            return result;
        }
        if (g2Inputs.Count != c.G2BatchConcepts)
        {
            result.Verdict = Hold;
            result.Reasons.Add($"G2 batch size mismatch: {g2Inputs.Count} != {c.G2BatchConcepts}");
            return result;
        }
        result.G2CandidatesConsidered = g2Sel.Considered;
        result.G2RejectedGuard = g2Sel.Rejected;
        result.G2Accepted = g2Inputs.Count;
        result.G2MaxVisited = g2Sel.MaxVisitedAccepted;

        var g1Results = new List<(PlanItem Item, GraphTraversal.Result Trav)>();
        foreach (var it in graphPlan)
            g1Results.Add((it, GraphTraversal.Ancestry(it.Qid, c.MaxDepth, c.VisitedNodeGuard, q => subclass.TryGetTargets(q, out var ts) ? ts : Array.Empty<long>())));

        // ---- lexical preparation (second pass over all lexical rows) ----
        var lexFold = new MultisetFoldV1();
        var langKind = new Dictionary<(string, string), long>();
        var s2Keys = new HashSet<(string, string)>();
        var s3Qids = new HashSet<long>();
        foreach (var p in servingFinal)
        {
            if (p.Op == "S2" && p.Lang != null && p.Measured) s2Keys.Add((p.Lang, p.Value));
            if (p.Op == "S3" && p.Qid != 0 && p.Measured) s3Qids.Add(p.Qid);
        }
        var s2Members = new Dictionary<(string, string), List<(long Qid, string Kind)>>();
        var s3Rows = new Dictionary<long, List<(string Lang, string Kind, string Value)>>();
        var a5Labels = new Dictionary<long, (string? En, string? Nb)>();
        bool a5Inconsistent = false;
        long lexCount = 0;
        foreach (var row in lexRows())
        {
            lexCount++;
            lexFold.Add(MultisetFoldV1.LexicalRow(row.Qid, row.Lang, row.Kind, row.Value));
            var lk = (row.Lang, row.Kind);
            langKind[lk] = langKind.TryGetValue(lk, out var lc) ? lc + 1 : 1;
            if (s2Keys.Contains((row.Lang, row.Value)))
            {
                if (!s2Members.TryGetValue((row.Lang, row.Value), out var ml)) { ml = new(); s2Members[(row.Lang, row.Value)] = ml; }
                ml.Add((row.Qid, row.Kind));
            }
            if (s3Qids.Contains(row.Qid))
            {
                if (!s3Rows.TryGetValue(row.Qid, out var rl)) { rl = new(); s3Rows[row.Qid] = rl; }
                rl.Add((row.Lang, row.Kind, row.Value));
            }
            if (row.Kind == "label" && (row.Lang == "en" || row.Lang == "nb") && instance.TargetCounts.ContainsKey(row.Qid))
            {
                if (!a5Labels.TryGetValue(row.Qid, out var lab)) { lab = (null, null); a5Labels[row.Qid] = lab; }
                if (row.Lang == "en")
                {
                    if (lab.En != null && lab.En != row.Value) a5Inconsistent = true;
                    else a5Labels[row.Qid] = (row.Value, lab.Nb);
                }
                else
                {
                    if (lab.Nb != null && lab.Nb != row.Value) a5Inconsistent = true;
                    else a5Labels[row.Qid] = (lab.En, row.Value);
                }
            }
        }
        if (a5Inconsistent)
        {
            result.Verdict = Hold;
            result.Reasons.Add("A5: multiple distinct label values for a single (target, language) label pair");
            return result;
        }
        result.LexicalRowCount = lexCount;
        result.InstanceRowCount = instance.RowCount;
        result.SubclassRowCount = subclass.RowCount;

        // ---- serving expected lines ----
        var expectedLines = new List<byte[]>();
        foreach (var p in servingFinal)
        {
            if (p.Op == "S1")
            {
                bool present = concept.TryGet(p.Qid, out bool in1, out bool in2);
                expectedLines.Add(ExpectedLine(p, present ? 1 : 0, WorkloadOracle.ConceptResultDigest(p.Qid, present, in1, in2)));
            }
            else if (p.Op == "S2")
            {
                if (!p.Measured || p.Lang == null) continue;
                long card = s2Members.TryGetValue((p.Lang, p.Value), out var ml) ? ml.Count : 0;
                expectedLines.Add(ExpectedLine(p, card,
                    ml == null || ml.Count == 0 ? WorkloadOracle.LexMissDigest(p.Lang, p.Value)
                                                 : WorkloadOracle.LexMembersDigest(ml)));
            }
            else if (p.Op == "S3")
            {
                long card = s3Rows.TryGetValue(p.Qid, out var rl) ? rl.Count : 0;
                expectedLines.Add(ExpectedLine(p, card, WorkloadOracle.LexicalRowsDigest(p.Qid, rl ?? new List<(string, string, string)>())));
            }
            else
            {
                EdgeTable t = p.Op == "S4" ? instance : subclass;
                long[] targets = t.TryGetTargets(p.Qid, out var tg) ? tg : Array.Empty<long>();
                expectedLines.Add(ExpectedLine(p, targets.Length, WorkloadOracle.AdjacencyDigest(targets)));
            }
        }

        // ---- graph files / expected (single sequenced G1 representation) ----
        var graphBytes = new List<byte[]>();
        var g1Sequenced = new List<(PlanItem Item, GraphTraversal.Result Trav)>();
        long gSeq = 0;
        foreach (var (it, trav) in g1Results)
        {
            var p = it with { Seq = gSeq++ };
            g1Sequenced.Add((p, trav));
            graphBytes.Add(ObjectLine(w =>
            {
                w.WriteString("op", p.Op);
                w.WriteString("stratum", p.Stratum);
                w.WriteNumber("seq", p.Seq);
                w.WriteBoolean("measured", true);
                w.WriteNumber("start", p.Qid);
            }));
        }
        var g2Start = gSeq;

        // G2 batch probe serializes the exact deterministic input QIDs with their source strata.
        graphBytes.Add(ObjectLine(w =>
        {
            w.WriteString("op", "G2");
            w.WriteString("stratum", "Batch");
            w.WriteNumber("seq", g2Start);
            w.WriteBoolean("measured", true);
            w.WritePropertyName("concepts");
            w.WriteStartArray();
            foreach (var (qid, src) in g2Inputs)
            {
                w.WriteStartObject();
                w.WriteNumber("qid", qid);
                w.WriteString("source_stratum", src);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }));

        var g1Expected = new List<byte[]>();
        foreach (var (p, trav) in g1Sequenced)
        {
            g1Expected.Add(ObjectLine(w =>
            {
                w.WriteString("op", p.Op);
                w.WriteNumber("seq", p.Seq);
                w.WriteBoolean("measured", true);
                w.WriteNumber("cardinality", trav.Discovered.Length);
                w.WriteNumber("visited", trav.VisitedCount);
                w.WriteString("digest", WorkloadOracle.G1Digest(trav.Discovered, trav.VisitedCount));
            }));
        }

        // Per-input G2 structural results plus the overall batch digest. Batch is
        // one 200-concept measured operation; per-input rows are correctness-only.
        var g2PerInputExpected = new List<byte[]>();
        var g2RowBytes = new List<byte[]>();
        for (int item = 0; item < g2Inputs.Count; item++)
        {
            var (q, source) = g2Inputs[item];
            var discovered = new SortedSet<long>();
            if (instance.TryGetTargets(q, out var targets))
            {
                foreach (long tg in targets)
                {
                    discovered.Add(tg);
                    var trav = GraphTraversal.Ancestry(tg, c.MaxDepth, c.VisitedNodeGuard, x => subclass.TryGetTargets(x, out var ts) ? ts : Array.Empty<long>());
                    foreach (long a in trav.Discovered) discovered.Add(a);
                }
            }
            long[] set = discovered.ToArray();
            string perDigest = WorkloadOracle.StructuralSetDigest(set);
            g2PerInputExpected.Add(ObjectLine(w =>
            {
                w.WriteString("op", "G2");
                w.WriteNumber("seq", g2Start);
                w.WriteString("kind", "PerInput");
                w.WriteBoolean("measured", false);
                w.WriteNumber("item", item);
                w.WriteNumber("qid", q);
                w.WriteString("source_stratum", source);
                w.WriteNumber("cardinality", set.Length);
                w.WriteString("digest", perDigest);
            }));
            var row = new Canon.Builder();
            row.AddLong(q).AddLong(set.Length);
            foreach (long d in set) row.AddLong(d);
            g2RowBytes.Add(row.ToArray());
        }
        string g2Digest = Canon.Sha256Hex(Concat(g2RowBytes));
        byte[] g2BatchExpected = ObjectLine(w =>
        {
            w.WriteString("op", "G2");
            w.WriteNumber("seq", g2Start);
            w.WriteString("kind", "Batch");
            w.WriteBoolean("measured", true);
            w.WriteNumber("cardinality", g2Inputs.Count);
            w.WriteString("digest", g2Digest);
        });

        // ---- analytical expected (exactly eight) ----
        var analytical = new List<byte[]>();
        analytical.Add(AnalyticalLine("A1-Concept", concept.Total, ConceptFold(concept)));
        analytical.Add(AnalyticalLine("A1-LexicalEntry", lexCount, lexFold.Digest()));
        analytical.Add(AnalyticalLine("A1-InstanceOf", instance.RowCount, EdgeFold(instance)));
        analytical.Add(AnalyticalLine("A1-SubclassOf", subclass.RowCount, EdgeFold(subclass)));
        analytical.Add(AnalyticalLine("A2", langKind.Count, SortedLangKindDigest(langKind)));
        analytical.Add(AnalyticalLine("A3", instance.TargetCounts.Count, SortedTargetCountDigest(instance.TargetCounts)));
        analytical.Add(AnalyticalLine("A4", subclass.TargetCounts.Count, SortedTargetCountDigest(subclass.TargetCounts)));
        analytical.Add(AnalyticalLine("A5", instance.TargetCounts.Count, A5Digest(instance.TargetCounts, a5Labels)));

        result.ServingLines = JoinLines(servingBytes);
        var expectedAll = new List<byte[]>(expectedLines);
        expectedAll.AddRange(g1Expected);
        expectedAll.AddRange(g2PerInputExpected);
        expectedAll.Add(g2BatchExpected);
        result.ExpectedLines = JoinLines(expectedAll);
        result.GraphLines = JoinLines(graphBytes);
        result.AnalyticalLines = JoinLines(analytical);
        result.MeasuredServingCount = servingMeasured;
        result.MeasuredG1Count = graphMeasured;
        result.G2BatchCount = g2Inputs.Count;
        return result;
    }

    private static byte[] ExpectedLine(PlanItem p, long cardinality, string digest)
        => ObjectLine(w =>
        {
            w.WriteString("op", p.Op);
            w.WriteNumber("seq", p.Seq);
            w.WriteBoolean("measured", p.Measured);
            w.WriteNumber("cardinality", cardinality);
            w.WriteString("digest", digest);
        });

    private static byte[] AnalyticalLine(string op, long cardinality, string digest)
        => ObjectLine(w =>
        {
            w.WriteString("op", op);
            w.WriteNumber("cardinality", cardinality);
            w.WriteString("digest", digest);
        });

    private static byte[] JoinLines(List<byte[]> lines)
    {
        long total = 0;
        foreach (var l in lines) total += l.Length;
        var buf = new byte[total];
        long at = 0;
        foreach (var l in lines)
        {
            Array.Copy(l, 0, buf, at, l.Length);
            at += l.Length;
        }
        return buf;
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        long total = 0;
        foreach (var p in parts) total += p.Length;
        var buf = new byte[total];
        long at = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, buf, at, p.Length);
            at += p.Length;
        }
        return buf;
    }

    private static string ConceptFold(ConceptTable c)
    {
        var fold = new MultisetFoldV1();
        for (int i = 0; i < c.Count; i++)
            fold.Add(MultisetFoldV1.ConceptRow(c.Qids[i], (c.Flags[i] & 1) != 0, (c.Flags[i] & 2) != 0));
        return fold.Digest();
    }

    private static string EdgeFold(EdgeTable e)
    {
        var fold = new MultisetFoldV1();
        foreach (var kv in e.Subjects)
        {
            if (e.TryGetTargets(kv, out var tg))
                foreach (long t in tg) fold.Add(MultisetFoldV1.EdgeRow(kv, t));
        }
        return fold.Digest();
    }

    private static string SortedLangKindDigest(Dictionary<(string, string), long> counts)
        => WorkloadOracle.AnalyticalRowsDigest(counts
            .OrderBy(k => k.Key.Item1, StringComparer.Ordinal)
            .ThenBy(k => k.Key.Item2, StringComparer.Ordinal)
            .Select(k => WorkloadOracle.LangKindCountRow(k.Key.Item1, k.Key.Item2, k.Value))
            .ToArray());

    private static string SortedTargetCountDigest(Dictionary<long, long> counts)
        => WorkloadOracle.AnalyticalRowsDigest(counts.OrderBy(k => k.Key).Select(k => WorkloadOracle.TargetCountRow(k.Key, k.Value)).ToArray());

    private static string A5Digest(Dictionary<long, long> targetCounts, Dictionary<long, (string? En, string? Nb)> labels)
        => WorkloadOracle.AnalyticalRowsDigest(targetCounts.OrderBy(k => k.Key).Select(k =>
        {
            labels.TryGetValue(k.Key, out var lab);
            return WorkloadOracle.A5Row(k.Key, k.Value, lab.En, lab.Nb);
        }).ToArray());

    private static readonly JsonWriterOptions Jwo = new() { SkipValidation = false };

    private static byte[] ObjectLine(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Jwo))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private static long CountPresent(ConceptTable c, List<string> qids)
    {
        long n = 0;
        foreach (var qs in qids)
        {
            if (qs.Length < 2 || qs[0] != 'Q') continue;
            if (long.TryParse(qs.AsSpan(1), out long q) && c.TryGet(q, out _, out _)) n++;
        }
        return n;
    }

    private static List<string> LoadSet(string fixturePath, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        if (!doc.RootElement.TryGetProperty("sets", out var sets) || !sets.TryGetProperty(name, out var arr))
            return new List<string>();
        return arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList();
    }

    private static long CountLexicalSurfaces(string fixturePath, LexicalStats lexical)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        if (!doc.RootElement.TryGetProperty("goldCases", out var cases)) return 0;
        long n = 0;
        foreach (var cse in cases.EnumerateArray())
        {
            string lang = cse.TryGetProperty("language", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            string term = cse.TryGetProperty("term", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (lang.Length == 0 || term.Length == 0) continue;
            if (lexical.Fanout.ContainsKey((lang, term))) n++;
        }
        return n;
    }

    private static long CountAmbiguousMulti(string fixturePath, ConceptTable concept)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        if (!doc.RootElement.TryGetProperty("goldCases", out var cases)) return 0;
        long n = 0;
        foreach (var cse in cases.EnumerateArray())
        {
            if (!cse.TryGetProperty("candidateQids", out var cands) || cands.ValueKind != JsonValueKind.Array) continue;
            int present = 0;
            foreach (var e in cands.EnumerateArray())
            {
                string? s = e.GetString();
                if (s != null && s.Length >= 2 && long.TryParse(s.AsSpan(1), out long q) && concept.TryGet(q, out _, out _)) present++;
            }
            if (present >= 2) n++;
        }
        return n;
    }
}
