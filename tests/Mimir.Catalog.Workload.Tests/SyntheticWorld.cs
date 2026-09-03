using System.Text.Json;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

/// <summary>
/// Builds a deterministic synthetic canonical corpus that satisfies every
/// frozen v1 stratum, enabling authoritative-path tests without the 155 GB
/// source. QID ranges:
///  T1∩T2 1..4000 · T1-only 4001..14000 · T2-only observed 14001..20000 ·
///  subclass deg1 subjects 16001..19000 · subclass deg>=2 subjects 19001..22000 ·
///  T2-only without lexical 15501..23000 · unobserved tail 24001..24020.
/// </summary>
public static class SyntheticWorld
{
    public sealed class Tables
    {
        public required ConceptTable Concept;
        public required LexicalStats Lexical;
        public required EdgeTable Instance;
        public required EdgeTable Subclass;
        public required List<ParquetLoader.LexRow> Rows;
        public required string FixturePath;
    }

    public static Tables Build(int highFanoutKeys = 380)
    {
        var concepts = new List<(long Qid, byte Flags)>();

        void AddRange(long from, long to, bool t1, bool t2)
        {
            byte f = (byte)((t1 ? 1 : 0) | (t2 ? 2 : 0));
            for (long q = from; q <= to; q++) concepts.Add((q, f));
        }

        AddRange(1, 4000, true, true);        // cap
        AddRange(4001, 14000, true, false);   // T1-only
        AddRange(14001, 20000, false, true);  // T2-only observed
        AddRange(20001, 23000, false, true);  // T2-only observed (no lexical)
        AddRange(24001, 24020, false, true);  // tail (greatest qids, ascending)

        concepts.Sort((a, b) => a.Qid.CompareTo(b.Qid));
        var qids = concepts.Select(c => c.Qid).ToArray();
        var flags = concepts.Select(c => c.Flags).ToArray();

        long cap = 0, t1Only = 0, t2Only = 0;
        foreach (var (q, f) in concepts)
        {
            bool a = (f & 1) != 0, b = (f & 2) != 0;
            if (a && b) cap++; else if (a) t1Only++; else t2Only++;
        }
        var tailQids = Enumerable.Range(24001, 20).Select(i => (long)i).ToList();

        var rows = new List<ParquetLoader.LexRow>();
        var fanout = new Dictionary<(string, string), long>();
        var withLex = new HashSet<long>();

        void Row(long qid, string lang, string kind, string value)
        {
            rows.Add(new ParquetLoader.LexRow(qid, lang, kind, value));
            fanout[(lang, value)] = fanout.TryGetValue((lang, value), out var c) ? c + 1 : 1;
            withLex.Add(qid);
        }

        // T1 labels (unique) for every T1 concept.
        for (long q = 1; q <= 14000; q++) Row(q, "en", "label", "L" + q);
        // T2-only with lexical labels.
        for (long q = 14001; q <= 15500; q++) Row(q, "en", "label", "T2L" + q);
        // A few nb labels (exercise nb bucket in A5/A2).
        for (long q = 1; q <= 200; q++) Row(q, "nb", "label", "NbL" + q);

        // Alias fanout strata over qids 1..3000.
        long qidAt(long i) => 1 + (i % 3000);
        for (long j = 1; j <= 4500; j++)
            for (int k = 0; k < 3; k++) Row(qidAt(j + k), "en", "alias", "dup" + j);       // fanout 3
        for (long k = 1; k <= 2000; k++)
            for (int r = 0; r < 10; r++) Row(qidAt(k + r), "en", "alias", "mid" + k);      // fanout 10
        for (long m = 1; m <= highFanoutKeys; m++)
            for (int r = 0; r < 60; r++) Row(qidAt(m + r), "en", "alias", "hi" + m);       // fanout 60

        // InstanceOf (P31) over T1 subjects: deg0 1..3000, deg1 3001..6000,
        // deg2 6001..9000, high (deg6) 9001..9500.
        var instAdj = new Dictionary<long, List<long>>();
        var instTargets = new Dictionary<long, long>();
        void AddInst(long s, long t)
        {
            if (!instAdj.TryGetValue(s, out var l)) { l = new List<long>(); instAdj[s] = l; }
            l.Add(t);
            instTargets[t] = instTargets.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        for (long s = 3001; s <= 6000; s++) AddInst(s, 16000 + (s % 3000));
        for (long s = 6001; s <= 9000; s++) { AddInst(s, 16000 + (s % 3000)); AddInst(s, 19000 + (s % 3000)); }
        for (long s = 9001; s <= 9500; s++)
            for (int k = 0; k < 6; k++) AddInst(s, 16000 + ((s + k) % 3000));

        // SubclassOf (P279): deg1 subjects 16001..19000 -> top; deg>=2 subjects
        // 19001..22000 -> two parents (top + rotating small hub).
        var subAdj = new Dictionary<long, List<long>>();
        var subTargets = new Dictionary<long, long>();
        void AddSub(long s, long t)
        {
            if (!subAdj.TryGetValue(s, out var l)) { l = new List<long>(); subAdj[s] = l; }
            l.Add(t);
            subTargets[t] = subTargets.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        for (long s = 16001; s <= 19000; s++) AddSub(s, 300_000);
        for (long s = 19001; s <= 22000; s++) { AddSub(s, 300_000); AddSub(s, 500_000 + (s % 7)); }

        // Temporary fixture file for continuity loading.
        string fixture = Path.Combine(Path.GetTempPath(), "mimir-synth-" + Guid.NewGuid().ToString("N") + ".json");
        var fixtureDoc = new
        {
            schema = "phase0-anchors-v1",
            sets = new Dictionary<string, string[]>
            {
                ["resolvedGold"] = new[] { "Q1", "Q2", "Q3" },
                ["goldUnion"] = new[] { "Q1", "Q99999" },
                ["ambiguousCand"] = new[] { "Q1", "Q2", "Q3", "Q5000" },
            },
            goldCases = new object[]
            {
                new { id = "c1", language = "en", term = "L1", candidateQids = new[] { "Q1", "Q2" } },
                new { id = "c2", language = "en", term = "missing-surface", candidateQids = (string[]?)null },
            },
        };
        File.WriteAllText(fixture, JsonSerializer.Serialize(fixtureDoc));

        return new Tables
        {
            Concept = new ConceptTable(qids, flags, qids.Length, t1Only, cap, t2Only, 20, tailQids),
            Lexical = new LexicalStats(rows.Count, withLex, fanout),
            Instance = new EdgeTable("InstanceOf", instAdj.ToDictionary(k => k.Key, k => k.Value.OrderBy(x => x).ToArray()), instTargets, instAdj.Values.Sum(v => v.Count)),
            Subclass = new EdgeTable("SubclassOf", subAdj.ToDictionary(k => k.Key, k => k.Value.OrderBy(x => x).ToArray()), subTargets, subAdj.Values.Sum(v => v.Count)),
            Rows = rows,
            FixturePath = fixture,
        };
    }

    public static void Cleanup(Tables t)
    {
        try { File.Delete(t.FixturePath); } catch { /* ignore */ }
    }
}
