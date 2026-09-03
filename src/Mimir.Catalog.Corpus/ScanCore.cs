using System.Diagnostics;
using System.Text;

namespace Mimir.Catalog.Corpus;

/// <summary>
/// Parser-level full-source counters whose totals must match the qualified
/// Phase-0 full-scale metrics exactly (used as the equivalence gate).
/// </summary>
public sealed class ScanTotals
{
    public long SourceRecords;
    public long Items;
    public long NonItems;
    public long Malformed;
    public long MissingOrDeleted;
    public long LabelEnPresent;
    public long LabelNbPresent;
    public long AliasEnStrings;
    public long AliasNbStrings;
    public long P31Pairs;
    public long P279Pairs;
}

/// <summary>
/// Streaming scan core with Phase-0 semantic equivalence. Reads a gzip Wikidata
/// JSON entity dump, decodes per line with strict UTF-8 (mirroring the Phase-0
/// per-line decode and counters), classifies each record, and accumulates the
/// parser-level totals. An optional per-item action is invoked for full Pass-A
/// evidence collection (T1/T2/lexical/edges); it is not used by the fixture.
/// </summary>
public static class ScanCore
{
    public sealed record ScanResult(
        ScanTotals Totals,
        long HashedBytes,
        string? MeasuredSha256,
        bool GzipTruncated,
        double ElapsedSeconds,
        bool DecodeFailureSeen);

    public static ScanResult Scan(
        string path,
        bool computeSha,
        long? expectedLength = null,
        Action<ParsedItem>? onItem = null,
        IProgress<ScanProgress>? progress = null)
    {
        var totals = new ScanTotals();
        long decodeFailures = 0;

        FileStream raw = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using (raw)
        {
            long actualLength = raw.Length;
            if (expectedLength.HasValue && actualLength != expectedLength.Value)
                throw new InvalidDataException($"source length mismatch: expected {expectedLength.Value}, actual {actualLength}");

            Stream compressed = raw;
            HashCountingStream? hasher = null;
            if (computeSha)
            {
                hasher = new HashCountingStream(raw);
                compressed = hasher;
            }

            var sw = Stopwatch.StartNew();
            var decoder = new UTF8Encoding(false, true);
            var reader = new GzipByteLineReader(compressed);
            long processedItems = 0;

            using (reader)
            {
                byte[] lineBuf = Array.Empty<byte>();
                while (reader.TryReadLine(out lineBuf))
                {
                    if (lineBuf.Length == 0)
                        continue;

                    string decoded;
                    try
                    {
                        decoded = decoder.GetString(lineBuf);
                    }
                    catch (DecoderFallbackException)
                    {
                        totals.Malformed++;
                        decodeFailures++;
                        continue;
                    }

                    string line = decoded.Trim();
                    if (line.Length == 0)
                        continue;
                    if (line is "[" or "]")
                        continue;
                    if (!line.StartsWith('{'))
                        continue;

                    EntityParseResult res = EntityParser.Parse(line);
                    switch (res.Outcome)
                    {
                        case EntityOutcome.Missing:
                            totals.MissingOrDeleted++;
                            break;
                        case EntityOutcome.Malformed:
                            totals.Malformed++;
                            if (res.CountsAsSourceRecord) totals.SourceRecords++;
                            break;
                        case EntityOutcome.NonItem:
                            totals.NonItems++;
                            totals.SourceRecords++;
                            break;
                        case EntityOutcome.Item:
                            totals.SourceRecords++;
                            totals.Items++;
                            AccumulateLexical(totals, res.Item!);
                            totals.P31Pairs += res.Item!.P31Targets.Count;
                            totals.P279Pairs += res.Item.P279Targets.Count;
                            onItem?.Invoke(res.Item);
                            processedItems++;
                            break;
                        default:
                            break;
                    }

                    if (progress != null && processedItems % 1_000_000 == 0 && processedItems > 0)
                        progress.Report(new ScanProgress(processedItems, totals, sw.Elapsed.TotalSeconds, hasher?.BytesRead ?? actualLength));
                }
            }

            string? sha = null;
            long hashedBytes = hasher?.BytesRead ?? actualLength;
            if (hasher != null)
            {
                sha = hasher.Sha256Hex();
                hasher.Dispose();
            }

            return new ScanResult(totals, hashedBytes, sha, reader.Truncated, sw.Elapsed.TotalSeconds, decodeFailures > 0);
        }
    }

    private static void AccumulateLexical(ScanTotals totals, ParsedItem item)
    {
        if (item.LabelEnPresent) totals.LabelEnPresent++;
        if (item.LabelNbPresent) totals.LabelNbPresent++;
        totals.AliasEnStrings += item.AliasEn.Count;
        totals.AliasNbStrings += item.AliasNb.Count;
    }
}

public sealed record ScanProgress(long ItemsProcessed, ScanTotals Totals, double ElapsedSeconds, long BytesRead);
