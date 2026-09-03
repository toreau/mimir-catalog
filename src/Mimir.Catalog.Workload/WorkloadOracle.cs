namespace Mimir.Catalog.Workload;

/// <summary>
/// Canonical result digests (candidate-neutral). Small/set outputs are sorted
/// canonically, encoded canonically and SHA-256'd. MultisetFoldV1 is used for
/// full relation scans. Physical candidate row order is never semantic.
/// </summary>
public static class WorkloadOracle
{
    public static byte[] LexMemberRow(long qid, string kind)
    {
        var b = new Canon.Builder();
        b.AddLong(qid).AddString(kind);
        return b.ToArray();
    }

    public static byte[] TargetCountRow(long target, long count)
    {
        var b = new Canon.Builder();
        b.AddLong(target).AddLong(count);
        return b.ToArray();
    }

    public static byte[] LangKindCountRow(string lang, string kind, long count)
    {
        var b = new Canon.Builder();
        b.AddString(lang).AddString(kind).AddLong(count);
        return b.ToArray();
    }

    public static byte[] A5Row(long target, long fanout, string? enLabel, string? nbLabel)
    {
        var b = new Canon.Builder();
        b.AddLong(target).AddLong(fanout);
        b.AddByte(enLabel == null ? (byte)0 : (byte)1);
        if (enLabel != null) b.AddString(enLabel);
        b.AddByte(nbLabel == null ? (byte)0 : (byte)1);
        if (nbLabel != null) b.AddString(nbLabel);
        return b.ToArray();
    }

    private static byte[] ConcatRows(IReadOnlyList<byte[]> sortedRows)
    {
        long total = 0;
        foreach (var r in sortedRows) total += r.Length;
        var buf = new byte[total];
        long at = 0;
        foreach (var r in sortedRows)
        {
            Array.Copy(r, 0, buf, at, r.Length);
            at += r.Length;
        }
        return buf;
    }

    private static string SizedRowsDigest(IReadOnlyList<byte[]> sortedRows)
    {
        var b = new Canon.Builder();
        b.AddLong(sortedRows.Count).AddRaw(ConcatRows(sortedRows));
        return b.ToSha256Hex();
    }

    /// <summary>S1 result digest: single Concept row or miss (qid retained in the miss image).</summary>
    public static string ConceptResultDigest(long qid, bool present, bool in1, bool in2)
    {
        var b = new Canon.Builder();
        if (present) b.AddLong(1).AddRaw(MultisetFoldV1.ConceptRow(qid, in1, in2));
        else b.AddLong(0).AddLong(qid);
        return b.ToSha256Hex();
    }

    /// <summary>S2 result digest: member rows sorted (Qid asc, LexKind ordinal asc).</summary>
    public static string LexMembersDigest(IReadOnlyList<(long Qid, string Kind)> members)
    {
        var rows = members
            .OrderBy(m => m.Qid)
            .ThenBy(m => m.Kind, StringComparer.Ordinal)
            .Select(m => LexMemberRow(m.Qid, m.Kind))
            .ToArray();
        return SizedRowsDigest(rows);
    }

    /// <summary>S3 result digest: full lexical rows sorted (Lang, LexKind, Value ordinal ascending).</summary>
    public static string LexicalRowsDigest(long qid, IReadOnlyList<(string Lang, string Kind, string Value)> rows)
    {
        var sorted = rows
            .OrderBy(r => r.Lang, StringComparer.Ordinal)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Value, StringComparer.Ordinal)
            .ToArray();
        var enc = new byte[sorted.Length][];
        for (int i = 0; i < sorted.Length; i++)
            enc[i] = MultisetFoldV1.LexicalRow(qid, sorted[i].Lang, sorted[i].Kind, sorted[i].Value);
        return SizedRowsDigest(enc);
    }

    /// <summary>S4/S5 adjacency result digest: targets sorted ascending.</summary>
    public static string AdjacencyDigest(long[] targetsAscending)
    {
        var rows = new byte[targetsAscending.Length][];
        for (int i = 0; i < targetsAscending.Length; i++) rows[i] = MultisetFoldV1.EdgeRow(0, targetsAscending[i]);
        return SizedRowsDigest(rows);
    }

    /// <summary>S2 miss result digest: zero members plus the queried key image.</summary>
    public static string LexMissDigest(string lang, string value)
    {
        var b = new Canon.Builder();
        b.AddLong(0).AddString(lang).AddString(value);
        return b.ToSha256Hex();
    }

    /// <summary>G1 result digest: discovered ancestors sorted ascending plus visited count.</summary>
    public static string G1Digest(long[] discoveredAscending, int visitedCount)
    {
        var b = new Canon.Builder();
        b.AddLong(discoveredAscending.Length);
        foreach (long q in discoveredAscending) b.AddLong(q);
        b.AddLong(visitedCount);
        return b.ToSha256Hex();
    }

    /// <summary>G2 per-input structural digest: sorted union of discovered structural QIDs.</summary>
    public static string StructuralSetDigest(long[] qidsAscending)
    {
        var b = new Canon.Builder();
        b.AddLong(qidsAscending.Length);
        foreach (long q in qidsAscending) b.AddLong(q);
        return b.ToSha256Hex();
    }

    /// <summary>Digest of an analytical result over canonical sorted rows (A2/A3/A4/A5).</summary>
    public static string AnalyticalRowsDigest(IReadOnlyList<byte[]> rowsInCanonicalOrder)
        => SizedRowsDigest(rowsInCanonicalOrder);
}
