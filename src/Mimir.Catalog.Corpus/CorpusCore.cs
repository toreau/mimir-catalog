namespace Mimir.Catalog.Corpus;

/// <summary>
/// Frozen benchmark-corpus contract for the Phase 1.1A.1 design.
/// Deterministic uniform QID sample (T1, p = 2.5%) plus full P279 endpoint
/// closure (T2). Kept purely as an evidence/identity contract; it is not a
/// production catalog schema.
/// </summary>
public static class CorpusContract
{
    public const string Domain = "mimir-catalog-corpus-v1";
    public const string UniformTag = "uniform";
    public const char Separator = '\x1f';
    public const string ContractVersion = "1";
    public const long Modulus = 1000;
    public const long Threshold = 25; // p = 2.5%
    public const double Fraction = 0.025;

    public static readonly IReadOnlyList<string> Languages = new[] { "en", "nb" };
    public const string LexicalKindLabel = "label";
    public const string LexicalKindAlias = "alias";

    /// <summary>Canonical descriptor string used to derive the corpus identity.</summary>
    public static string Descriptor() =>
        $"{{\"contractVersion\":\"{ContractVersion}\",\"domain\":\"{Domain}\",\"tag\":\"{UniformTag}\"," +
        $"\"fraction\":{Fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
        $"\"modulus\":{Modulus},\"threshold\":{Threshold},\"languages\":[\"en\",\"nb\"]}}";
}

/// <summary>
/// Wikidata QID/property-id parsing matching the frozen Phase-0 grammar
/// ^Q[1-9][0-9]*$ / ^P[1-9][0-9]*$. Numeric (Int64) representation internally.
/// </summary>
public static class Qid
{
    public static bool IsValidItemId(ReadOnlySpan<char> s) => TryParse(s, 'Q', out _);
    public static bool IsValidPropertyId(ReadOnlySpan<char> s) => TryParse(s, 'P', out _);

    public static bool TryParse(ReadOnlySpan<char> s, out long value) => TryParse(s, 'Q', out value);

    private static bool TryParse(ReadOnlySpan<char> s, char prefix, out long value)
    {
        value = 0;
        if (s.Length < 2 || s[0] != prefix)
            return false;
        if (s[1] < '1' || s[1] > '9')
            return false;
        long acc = 0;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return false;
            acc = checked(acc * 10 + (c - '0'));
        }
        value = acc;
        return true;
    }
}

/// <summary>
/// Deterministic T1 membership hash. SHA-256 over
/// UTF8(domain + 0x1F + "uniform" + 0x1F + decimal(Qid)); first 8 bytes are
/// interpreted as an unsigned big-endian 64-bit integer; membership is
/// h mod 1000 &lt; 25. No GetHashCode, no randomness, no source order.
/// </summary>
public static class CorpusHash
{
    private static readonly byte[] Prefix =
        System.Text.Encoding.ASCII.GetBytes(CorpusContract.Domain + CorpusContract.Separator + CorpusContract.UniformTag + CorpusContract.Separator);

    public static ulong Hash64(long qid)
    {
        Span<byte> input = stackalloc byte[Prefix.Length + 20];
        Prefix.CopyTo(input);
        int idx = Prefix.Length;
        Span<byte> digits = stackalloc byte[20];
        int c = 0;
        long v = qid;
        do
        {
            digits[c++] = (byte)('0' + (v % 10));
            v /= 10;
        } while (v > 0);
        for (int i = c - 1; i >= 0; i--)
            input[idx++] = digits[i];

        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(input[..idx], hash);
        ulong res = 0;
        for (int i = 0; i < 8; i++)
            res = (res << 8) | hash[i];
        return res;
    }

    public static long Bucket(long qid) => (long)(Hash64(qid) % (ulong)CorpusContract.Modulus);

    public static bool IsT1(long qid) => Bucket(qid) < CorpusContract.Threshold;
}

/// <summary>
/// Corpus identity derived from the frozen contract descriptor plus the pinned
/// source identity. Deterministic; a fraction/rule change yields a new id.
/// </summary>
public static class CorpusIdentity
{
    public static string ComputeId()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(CorpusContract.Descriptor());
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash[..16]);
    }
}

/// <summary>
/// Authoritative qualified Phase-0 source identity used for Pass-A verification.
/// </summary>
public sealed record SourceIdentity(
    string Url,
    string Sha256,
    long ContentLength,
    string PinnedRetrievedTs)
{
    public const string ExpectedPath = "/tmp/wikidata-latest-all.json.gz.partial";
    public const string ExpectedSha256 = "5fc1f212c9e5cbf681abca84ecf1ff8dde5cd1b1f78582d3db653f9d3a1f655f";
    public const long ExpectedContentLength = 155690403548;
    public const string PinnedRetrieved = "2026-08-29T18:30:00Z";

    public static SourceIdentity PinnedSource() =>
        new("https://dumps.wikimedia.org/wikidatawiki/entities/latest-all.json.gz",
            ExpectedSha256, ExpectedContentLength, PinnedRetrieved);
}
