using System.Text;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Deterministic SHA-256 ranking selection and probe ordering for the frozen
/// probe construction rules. No RNG APIs, no process hashes. Every stratum is
/// internally SHA-ranked ascending before round-robin interleaving.
/// </summary>
public static class WorkloadSelection
{
    public readonly record struct QidItem(long Qid, Canon.Hash256 Hash);

    public readonly record struct LexItem(string Lang, string Value, Canon.Hash256 Hash);

    public static string QidIdentity(long qid) => qid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static Canon.Hash256 RankHash(string domain, string operation, string stratum, long qid)
        => Canon.Hash256.Of(Canon.SelectorBytes(domain, operation, stratum, QidIdentity(qid)));

    public static Canon.Hash256 RankHash(string domain, string operation, string stratum, string lang, string value)
        => Canon.Hash256.Of(Canon.SelectorBytes(domain, operation, stratum, Canon.LexicalIdentity(lang, value)));

    /// <summary>Select the n lowest-ranked candidates; returned ascending by (hash, qid).</summary>
    public static QidItem[] RankTopQids(IReadOnlyList<long> pool, string domain, string operation, string stratum, int n)
    {
        if (pool.Count < n)
            throw new InvalidOperationException($"pool insufficient: {operation}/{stratum} needs {n}, has {pool.Count}");
        var items = new QidItem[pool.Count];
        for (int i = 0; i < pool.Count; i++)
            items[i] = new QidItem(pool[i], RankHash(domain, operation, stratum, pool[i]));
        Array.Sort(items, (a, b) =>
        {
            int c = a.Hash.CompareTo(b.Hash);
            return c != 0 ? c : a.Qid.CompareTo(b.Qid);
        });
        var result = new QidItem[n];
        Array.Copy(items, result, n);
        return result;
    }

    public static LexItem[] RankTopLex(IReadOnlyList<(string Lang, string Value)> pool, string domain, string operation, string stratum, int n)
    {
        if (pool.Count < n)
            throw new InvalidOperationException($"pool insufficient: {operation}/{stratum} needs {n}, has {pool.Count}");
        var items = new LexItem[pool.Count];
        for (int i = 0; i < pool.Count; i++)
            items[i] = new LexItem(pool[i].Lang, pool[i].Value, RankHash(domain, operation, stratum, pool[i].Lang, pool[i].Value));
        Array.Sort(items, (a, b) =>
        {
            int c = a.Hash.CompareTo(b.Hash);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Lang + "\u001f" + a.Value, b.Lang + "\u001f" + b.Value);
        });
        var result = new LexItem[n];
        Array.Copy(items, result, n);
        return result;
    }

    /// <summary>Deterministic concept misses proven absent from the concept table.</summary>
    public static QidItem[] GenerateConceptMisses(Func<long, bool> isPresent, string missDomain, string operation, string stratum, int count)
    {
        var accepted = new List<QidItem>(count);
        long counter = 0;
        var seen = new HashSet<long>();
        while (accepted.Count < count)
        {
            byte[] sel = Canon.SelectorBytes(missDomain, operation, stratum, counter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            byte[] h = Canon.Sha256Bytes(sel);
            ulong u = 0;
            for (int i = 0; i < 8; i++) u = (u << 8) | h[i];
            long candidate = (long)(u & 0x7FFFFFFFFFFFFFFFUL);
            counter++;
            if (candidate <= 0 || seen.Contains(candidate)) continue;
            if (isPresent(candidate)) continue; // proven absent required
            seen.Add(candidate);
            accepted.Add(new QidItem(candidate, Canon.Hash256.Of(sel)));
        }
        accepted.Sort((a, b) =>
        {
            int c = a.Hash.CompareTo(b.Hash);
            return c != 0 ? c : a.Qid.CompareTo(b.Qid);
        });
        return accepted.ToArray();
    }

    /// <summary>Deterministic lexical misses proven absent from the lexical (Lang,Value) domain.</summary>
    public static LexItem[] GenerateLexicalMisses(
        Func<string, string, bool> isPresent, string missDomain, string operation, string stratum, int count,
        IReadOnlyList<string> languages)
    {
        var accepted = new List<LexItem>(count);
        long counter = 0;
        var seen = new HashSet<string>();
        while (accepted.Count < count)
        {
            byte[] sel = Canon.SelectorBytes(missDomain, operation, stratum, counter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            byte[] h = Canon.Sha256Bytes(sel);
            counter++;
            string lang = languages[(int)(counter % languages.Count)];
            string value = "mc-miss-" + Convert.ToHexStringLower(h).Substring(0, 24);
            string key = lang + "\u001f" + value;
            if (seen.Contains(key)) continue;
            if (isPresent(lang, value)) continue; // proven zero rows required
            seen.Add(key);
            accepted.Add(new LexItem(lang, value, Canon.Hash256.Of(sel)));
        }
        accepted.Sort((a, b) =>
        {
            int c = a.Hash.CompareTo(b.Hash);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Lang + "\u001f" + a.Value, b.Lang + "\u001f" + b.Value);
        });
        return accepted.ToArray();
    }

    /// <summary>
    /// Round-robin interleave of stratum lists in frozen stratum order; when a
    /// stratum is exhausted the remaining strata continue. Result is the
    /// published measured sequence.
    /// </summary>
    public static List<T> RoundRobinInterleave<T>(IReadOnlyList<T[]> strataInOrder)
    {
        int m = strataInOrder.Count;
        var positions = new int[m];
        int remaining = 0;
        foreach (var s in strataInOrder) remaining += s.Length;
        var result = new List<T>(remaining);
        while (remaining > 0)
        {
            bool any = false;
            for (int i = 0; i < m; i++)
            {
                if (positions[i] < strataInOrder[i].Length)
                {
                    result.Add(strataInOrder[i][positions[i]++]);
                    remaining--;
                    any = true;
                }
            }
            if (!any) break; // safety
        }
        return result;
    }
}
