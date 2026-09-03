using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Reads the accepted canonical Pass-B Parquet relations into the neutral
/// in-memory reference tables. Read-only; never mutates the corpus.
/// </summary>
public static class ParquetLoader
{
    public readonly record struct LexRow(long Qid, string Lang, string Kind, string Value);

    /// <summary>Streaming lexical row iterator (deterministic file order).</summary>
    public static IEnumerable<LexRow> EnumerateLexical(string passBDir)
    {
        string path = Path.Combine(passBDir, "lexical_entry.parquet");
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
                Await(rg.ReadAsync<long>(f0, new Memory<long>(q)));
                Await(rg.ReadAsync(f1, new Memory<string>(lang)));
                Await(rg.ReadAsync(f2, new Memory<string>(kind)));
                Await(rg.ReadAsync(f3, new Memory<string>(value)));
                for (int i = 0; i < n; i++)
                    yield return new LexRow(q[i], lang[i], kind[i], value[i]);
            }
        }
        finally
        {
            Await(reader.DisposeAsync());
        }
    }

    public static ConceptTable LoadConcept(string passBDir)
    {
        string path = Path.Combine(passBDir, "concept.parquet");
        var rowQids = new List<long>();
        var rowIn1 = new List<bool>();
        var rowIn2 = new List<bool>();
        long t1 = 0, cap = 0, t2 = 0;
        return ReadConceptGroups(path, rowQids, rowIn1, rowIn2, ref t1, ref cap, ref t2);
    }

    private static ConceptTable ReadConceptGroups(string path, List<long> rowQids, List<bool> rowIn1, List<bool> rowIn2, ref long t1, ref long cap, ref long t2)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            var f2 = (DataField)reader.Schema.DataFields[2];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var q = new long[n];
                var a = new bool[n];
                var b = new bool[n];
                Await(rg.ReadAsync<long>(f0, new Memory<long>(q)));
                Await(rg.ReadAsync<bool>(f1, new Memory<bool>(a)));
                Await(rg.ReadAsync<bool>(f2, new Memory<bool>(b)));
                for (int i = 0; i < n; i++)
                {
                    rowQids.Add(q[i]);
                    rowIn1.Add(a[i]);
                    rowIn2.Add(b[i]);
                    if (a[i] && b[i]) cap++;
                    else if (a[i]) t1++;
                    else t2++;
                }
            }
        }
        finally
        {
            Await(reader.DisposeAsync());
        }

        int totalRows = rowQids.Count;
        // Detect the unobserved T2 tail as the trailing 20 file rows.
        const int tailExpected = 20;
        int tailStart = Math.Max(0, totalRows - tailExpected);
        var tail = new List<long>(tailExpected);
        for (int i = tailStart; i < totalRows; i++)
        {
            if (!rowIn2[i] || rowIn1[i])
                throw new InvalidDataException("Concept tail detection failed: trailing rows are not InT2-only");
            tail.Add(rowQids[i]);
        }
        for (int i = 1; i < tail.Count; i++)
            if (tail[i] <= tail[i - 1])
                throw new InvalidDataException("Concept tail detection failed: tail QIDs not strictly ascending");

        long[] sorted = rowQids.ToArray();
        byte[] flags = new byte[totalRows];
        for (int i = 0; i < totalRows; i++)
            flags[i] = (byte)((rowIn1[i] ? 1 : 0) | (rowIn2[i] ? 2 : 0));
        Array.Sort(sorted, flags);

        return new ConceptTable(sorted, flags, totalRows, t1, cap, t2, tailExpected, tail);
    }

    public static LexicalStats LoadLexical(string passBDir)
    {
        string path = Path.Combine(passBDir, "lexical_entry.parquet");
        var fanout = new Dictionary<(string, string), long>();
        var withLex = new HashSet<long>();
        long rows = 0;
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
                Await(rg.ReadAsync<long>(f0, new Memory<long>(q)));
                Await(rg.ReadAsync(f1, new Memory<string>(lang)));
                Await(rg.ReadAsync(f2, new Memory<string>(kind)));
                Await(rg.ReadAsync(f3, new Memory<string>(value)));
                for (int i = 0; i < n; i++)
                {
                    var key = (lang[i], value[i]);
                    fanout[key] = fanout.TryGetValue(key, out var c) ? c + 1 : 1;
                    withLex.Add(q[i]);
                    rows++;
                }
            }
        }
        finally
        {
            Await(reader.DisposeAsync());
        }
        return new LexicalStats(rows, withLex, fanout);
    }

    public static EdgeTable LoadEdge(string relation, string passBDir)
    {
        string file = relation switch
        {
            "InstanceOf" => "instance_of.parquet",
            "SubclassOf" => "subclass_of.parquet",
            _ => throw new ArgumentOutOfRangeException(nameof(relation)),
        };
        string path = Path.Combine(passBDir, file);
        var adj = new Dictionary<long, List<long>>();
        var targetCounts = new Dictionary<long, long>();
        long rows = 0;
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var f0 = (DataField)reader.Schema.DataFields[0];
            var f1 = (DataField)reader.Schema.DataFields[1];
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                using var rg = reader.OpenRowGroupReader(g);
                int n = (int)rg.RowCount;
                var s = new long[n];
                var t = new long[n];
                Await(rg.ReadAsync<long>(f0, new Memory<long>(s)));
                Await(rg.ReadAsync<long>(f1, new Memory<long>(t)));
                for (int i = 0; i < n; i++)
                {
                    if (!adj.TryGetValue(s[i], out var list))
                    {
                        list = new List<long>();
                        adj[s[i]] = list;
                    }
                    list.Add(t[i]);
                    targetCounts[t[i]] = targetCounts.TryGetValue(t[i], out var c) ? c + 1 : 1;
                    rows++;
                }
            }
        }
        finally
        {
            Await(reader.DisposeAsync());
        }

        var adjFinal = new Dictionary<long, long[]>(adj.Count);
        foreach (var kv in adj)
        {
            kv.Value.Sort();
            adjFinal[kv.Key] = kv.Value.ToArray();
        }
        return new EdgeTable(relation, adjFinal, targetCounts, rows);
    }

    /// <summary>Stream a second lexical pass for targeted row collection (deterministic order = file order).</summary>
    public static void StreamLexical(string passBDir, Action<long, string, string, string> row)
    {
        string path = Path.Combine(passBDir, "lexical_entry.parquet");
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
                Await(rg.ReadAsync<long>(f0, new Memory<long>(q)));
                Await(rg.ReadAsync(f1, new Memory<string>(lang)));
                Await(rg.ReadAsync(f2, new Memory<string>(kind)));
                Await(rg.ReadAsync(f3, new Memory<string>(value)));
                for (int i = 0; i < n; i++) row(q[i], lang[i], kind[i], value[i]);
            }
        }
        finally
        {
            Await(reader.DisposeAsync());
        }
    }

    private static void Await(ValueTask t) => t.GetAwaiter().GetResult();
    private static void Await<T>(ValueTask<T> t) => t.GetAwaiter().GetResult();
}
