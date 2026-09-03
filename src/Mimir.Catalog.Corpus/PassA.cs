using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mimir.Catalog.Corpus;

public sealed class PassAOptions
{
    public required string SourcePath { get; init; }
    public required string WorkDir { get; init; }
    public bool SkipSha { get; init; }
    /// <summary>Fixture mode: no pinned-length gate and no inline SHA (synthetic/local inputs only).</summary>
    public bool IsFixture { get; init; }
}

public enum PassAStateKind { Pending, Running, Complete, Failed }

public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}

public sealed class PassAEvidence
{
    public ScanTotals Totals = new();
    public long T1;
    public long T2;
    public long T1IntersectT2;
    public long T2Only;
    public long T1UnionT2;
    public long P279Subjects;
    public long P279Objects;
    public long P31WithT1;
    public long T1LabelEn, T1LabelNb, T1AliasEn, T1AliasNb;
    public long T2OnlyLabelEnPresent, T2OnlyLabelNbPresent;
    public long EndpointFileBytes;
    public double WallSeconds;
    public long PeakRssSampledKb;
    public long TempDiskPeakBytes;
    public string? MeasuredSha256;
    public bool ShaFreshlyMeasured;
    public long HashedBytes;
}

/// <summary>
/// Pass A: one full source scan collecting the structural evidence needed to
/// decide whether Pass B should be planned. It never materializes the final
/// benchmark corpus. Uses temp SQLite purely as a bounded-memory aggregation
/// detail (not a storage decision); no DuckDB fallback in this slice.
/// </summary>
public static class PassA
{
    public static PassAEvidence Run(PassAOptions opts)
    {
        string work = opts.WorkDir;
        Directory.CreateDirectory(work);
        var log = Console.Out;
        var identity = SourceIdentity.PinnedSource();

        WriteState(work, PassAStateKind.Running);

        long actualLen = new FileInfo(opts.SourcePath).Length;
        if (!opts.IsFixture && actualLen != identity.ContentLength)
            throw new InvalidDataException($"source size mismatch: expected {identity.ContentLength}, actual {actualLen}");

        bool computeSha = !opts.SkipSha && !opts.IsFixture;
        long? expectedLength = opts.IsFixture ? null : identity.ContentLength;

        string dbPath = Path.Combine(work, "aggregation.sqlite");
        using var sink = new SqliteSink(dbPath);
        sink.Begin();

        long t1 = 0, t1LabelEn = 0, t1LabelNb = 0, t1AliasEn = 0, t1AliasNb = 0;
        long p31WithT1 = 0;
        long labelEnLenSum = 0, labelEnLenCount = 0;
        long labelNbLenSum = 0, labelNbLenCount = 0;
        long aliasEnLenSum = 0, aliasEnLenCount = 0;
        long aliasNbLenSum = 0, aliasNbLenCount = 0;
        var endpoints = new HashSet<long>();
        var outDegP31 = new Dictionary<long, long>();
        var outDegP279 = new Dictionary<long, long>();
        long peakRssKb = 0, tempPeakBytes = 0;
        long sinceCommit = 0;
        const long CommitEveryItems = 5_000_000;

        void TickProgress(ScanProgress p)
        {
            long rssKb = GC.GetTotalMemory(false) / 1024;
            if (rssKb > peakRssKb) peakRssKb = rssKb;
            long dbBytes = new FileInfo(dbPath).Exists ? new FileInfo(dbPath).Length : 0;
            if (dbBytes > tempPeakBytes) tempPeakBytes = dbBytes;
            log.WriteLine($"progress items={p.ItemsProcessed} elapsed={p.ElapsedSeconds:F0}s rate={(long)(p.ItemsProcessed / Math.Max(1.0, p.ElapsedSeconds))}/s dbBytes={dbBytes} hashedBytes={p.BytesRead}");
            log.Flush();
        }

        var prog = new SyncProgress<ScanProgress>(TickProgress);
        var result = ScanCore.Scan(
            opts.SourcePath,
            computeSha: computeSha,
            expectedLength: expectedLength,
            onItem: item =>
            {
                long qid = item.Qid;
                bool isT1 = CorpusHash.IsT1(qid);
                if (isT1)
                {
                    t1++;
                    if (item.LabelEnPresent) { t1LabelEn++; labelEnLenSum += item.LabelEnValue?.Length ?? 0; labelEnLenCount++; }
                    if (item.LabelNbPresent) { t1LabelNb++; labelNbLenSum += item.LabelNbValue?.Length ?? 0; labelNbLenCount++; }
                    t1AliasEn += item.AliasEn.Count;
                    t1AliasNb += item.AliasNb.Count;
                    foreach (var a in item.AliasEn) { aliasEnLenSum += a.Length; aliasEnLenCount++; }
                    foreach (var a in item.AliasNb) { aliasNbLenSum += a.Length; aliasNbLenCount++; }
                    p31WithT1 += item.P31Targets.Count;
                }
                else
                {
                    int flags = PassALogic.PresenceFlags(item.LabelEnPresent, item.LabelNbPresent);
                    if (flags != 0)
                        sink.AddPresence(qid, flags);
                }

                PassALogic.BumpDegree(outDegP31, item.P31Targets.Count);
                PassALogic.BumpDegree(outDegP279, item.P279Targets.Count);
                foreach (long t in item.P31Targets) sink.AddP31(qid, t);
                // T2 is the endpoint population of actual P279 edges: a subject
                // enters T2 only when it carries at least one P279 target.
                if (item.P279Targets.Count > 0)
                {
                    PassALogic.AddP279Endpoints(endpoints, qid, item.P279Targets);
                    foreach (long t in item.P279Targets) sink.AddP279(qid, t);
                }

                // Periodic commit bounds the single-transaction pager footprint.
                if (++sinceCommit % CommitEveryItems == 0)
                {
                    sink.Commit();
                    sink.Begin();
                    long dbBytesNow = new FileInfo(dbPath).Exists ? new FileInfo(dbPath).Length : 0;
                    if (dbBytesNow > tempPeakBytes) tempPeakBytes = dbBytesNow;
                    log.WriteLine($"commit checkpoint at {sinceCommit} items dbBytes={dbBytesNow}");
                    log.Flush();
                }
            },
            progress: prog);

        if (computeSha)
        {
            if (result.HashedBytes != identity.ContentLength)
                throw new InvalidDataException(
                    $"compressed bytes consumed {result.HashedBytes} != source length {identity.ContentLength}; " +
                    "trailing data or multi-member gzip is unsupported by this reader");
            if (result.MeasuredSha256 != identity.Sha256)
                throw new InvalidDataException($"source SHA-256 mismatch: expected {identity.Sha256}, measured {result.MeasuredSha256}");
        }
        if (result.GzipTruncated)
            throw new InvalidDataException("gzip stream terminated before end of member");

        // ---- finalize ----
        var ev = new PassAEvidence();
        ev.Totals = result.Totals;
        ev.T1 = t1;
        ev.P31WithT1 = p31WithT1;
        ev.T1LabelEn = t1LabelEn; ev.T1LabelNb = t1LabelNb; ev.T1AliasEn = t1AliasEn; ev.T1AliasNb = t1AliasNb;
        ev.WallSeconds = result.ElapsedSeconds;
        ev.MeasuredSha256 = result.MeasuredSha256;
        ev.ShaFreshlyMeasured = !opts.SkipSha;
        ev.HashedBytes = result.HashedBytes;
        ev.PeakRssSampledKb = peakRssKb;
        ev.TempDiskPeakBytes = tempPeakBytes;

        long[] sortedEndpoints = endpoints.OrderBy(x => x).ToArray();
        ev.T2 = sortedEndpoints.LongLength;
        string epPath = Path.Combine(work, "t2-endpoints.bin");
        ev.EndpointFileBytes = T2Persistence.WriteEndpoints(epPath, sortedEndpoints);

        ev.T1IntersectT2 = endpoints.Count(CorpusHash.IsT1);
        (ev.T2Only, ev.T1UnionT2) = PassALogic.TierArithmetic(ev.T1, ev.T2, ev.T1IntersectT2);

        ev.P279Subjects = sink.QueryLong("SELECT count(DISTINCT s) FROM p279");
        ev.P279Objects = sink.QueryLong("SELECT count(DISTINCT o) FROM p279");

        using (var cmd = sink.CreateCommand("SELECT qid, flags FROM presence"))
        using (var rd = cmd.ExecuteReader())
        {
            long en = 0, nb = 0;
            while (rd.Read())
            {
                long q = rd.GetInt64(0);
                if (!endpoints.Contains(q)) continue;
                int flags = rd.GetInt32(1);
                if ((flags & 1) != 0) en++;
                if ((flags & 2) != 0) nb++;
            }
            ev.T2OnlyLabelEnPresent = en;
            ev.T2OnlyLabelNbPresent = nb;
        }

        var p31Fan = ComputeTargetFanout(sink, "p31", work, "p31");
        var p279Fan = ComputeTargetFanout(sink, "p279", work, "p279");
        var p31Out = PassALogic.DegreeSummary(outDegP31);
        var p279Out = PassALogic.DegreeSummary(outDegP279);

        sink.Commit();

        WriteEvidence(opts, ev, p31Fan, p279Fan, p31Out, p279Out,
            labelEnLenSum, labelEnLenCount, labelNbLenSum, labelNbLenCount,
            aliasEnLenSum, aliasEnLenCount, aliasNbLenSum, aliasNbLenCount);

        sink.Dispose();
        File.Delete(dbPath);

        WriteState(work, PassAStateKind.Complete);
        return ev;
    }

    private sealed record FanoutResult(long Distinct, long Total, long Min, long Max, double Median, double P90, double P95, double P99, string ArtifactPath);

    private static FanoutResult ComputeTargetFanout(SqliteSink sink, string table, string work, string name)
    {
        string f = $"f_{name}";
        sink.Execute($"CREATE TABLE {f} AS SELECT o AS target, count(*) AS c FROM {table} GROUP BY o");
        long distinct = sink.QueryLong($"SELECT count(*) FROM {f}");
        long min = sink.QueryLong($"SELECT min(c) FROM {f}");
        long max = sink.QueryLong($"SELECT max(c) FROM {f}");

        long Quantile(double q)
        {
            long offset = (long)((distinct - 1) * q);
            using var cmd = sink.CreateCommand($"SELECT c FROM {f} ORDER BY c LIMIT 1 OFFSET $k");
            cmd.Parameters.AddWithValue("$k", offset);
            var r = cmd.ExecuteScalar();
            return r == null || r == DBNull.Value ? 0 : Convert.ToInt64(r);
        }

        double med = Quantile(0.50), p90 = Quantile(0.90), p95 = Quantile(0.95), p99 = Quantile(0.99);

        string probePath = Path.Combine(work, $"probe-hints.{name}.jsonl");
        using (var cmd = sink.CreateCommand($"SELECT target, c FROM {f} ORDER BY c DESC, target ASC LIMIT 200"))
        using (var rd = cmd.ExecuteReader())
        using (var w = File.CreateText(probePath))
        {
            int rank = 0;
            while (rd.Read())
            {
                rank++;
                w.WriteLine(JsonSerializer.Serialize(new { distribution = name, rank, target = rd.GetInt64(0), fanout = rd.GetInt64(1) }));
            }
        }

        sink.Execute($"DROP TABLE {f}");
        return new FanoutResult(distinct, distinct, min, max, med, p90, p95, p99, probePath);
    }

    private static void WriteEvidence(
        PassAOptions opts, PassAEvidence ev,
        FanoutResult p31Fan, FanoutResult p279Fan,
        PassALogic.DegreeStats p31Out, PassALogic.DegreeStats p279Out,
        long labelEnLenSum, long labelEnLenCount, long labelNbLenSum, long labelNbLenCount,
        long aliasEnLenSum, long aliasEnLenCount, long aliasNbLenSum, long aliasNbLenCount)
    {
        var identity = SourceIdentity.PinnedSource();
        var doc = new Dictionary<string, object?>
        {
            ["identity"] = new Dictionary<string, object?>
            {
                ["corpusContractVersion"] = CorpusContract.ContractVersion,
                ["t1Domain"] = CorpusContract.Domain,
                ["t1Tag"] = CorpusContract.UniformTag,
                ["t1Algorithm"] = "sha256:first8BE:mod1000",
                ["t1Threshold"] = CorpusContract.Threshold,
                ["t1Modulus"] = CorpusContract.Modulus,
                ["corpusId"] = CorpusIdentity.ComputeId(),
                ["sourcePath"] = opts.SourcePath,
                ["sourceUrl"] = identity.Url,
                ["sourceContentLength"] = identity.ContentLength,
                ["sourceSha256"] = identity.Sha256,
                ["sourceShaFreshlyMeasured"] = ev.ShaFreshlyMeasured,
                ["sourceShaMeasuredValue"] = ev.MeasuredSha256,
                ["sourceShaStatus"] = ev.ShaFreshlyMeasured ? "freshly-measured" : "inherited-from-manifest",
            },
            ["parserTotals"] = new Dictionary<string, object?>
            {
                ["source_entity_records"] = ev.Totals.SourceRecords,
                ["item_records"] = ev.Totals.Items,
                ["projected_qids"] = ev.Totals.Items,
                ["non_item_records_skipped"] = ev.Totals.NonItems,
                ["malformed_records"] = ev.Totals.Malformed,
                ["missing_or_deleted"] = ev.Totals.MissingOrDeleted,
                ["label_en"] = ev.Totals.LabelEnPresent,
                ["label_nb"] = ev.Totals.LabelNbPresent,
                ["alias_en"] = ev.Totals.AliasEnStrings,
                ["alias_nb"] = ev.Totals.AliasNbStrings,
                ["p31_edges"] = ev.Totals.P31Pairs,
                ["p279_edges"] = ev.Totals.P279Pairs,
            },
            ["tiers"] = new Dictionary<string, object?>
            {
                ["t1"] = ev.T1,
                ["t2"] = ev.T2,
                ["t1_intersect_t2"] = ev.T1IntersectT2,
                ["t2_only"] = ev.T2Only,
                ["t1_union_t2"] = ev.T1UnionT2,
            },
            ["structural"] = new Dictionary<string, object?>
            {
                ["unique_p279_pairs"] = ev.Totals.P279Pairs,
                ["p279_distinct_subjects"] = ev.P279Subjects,
                ["p279_distinct_objects"] = ev.P279Objects,
                ["p31_total_pairs"] = ev.Totals.P31Pairs,
                ["p31_pairs_with_subject_in_t1"] = ev.P31WithT1,
            },
            ["lexical"] = new Dictionary<string, object?>
            {
                ["t1_label_en_present"] = ev.T1LabelEn,
                ["t1_label_nb_present"] = ev.T1LabelNb,
                ["t1_alias_en_strings"] = ev.T1AliasEn,
                ["t1_alias_nb_strings"] = ev.T1AliasNb,
                ["t2_only_label_en_present"] = ev.T2OnlyLabelEnPresent,
                ["t2_only_label_nb_present"] = ev.T2OnlyLabelNbPresent,
                ["t1_label_en_mean_len"] = labelEnLenCount == 0 ? 0 : labelEnLenSum / (double)labelEnLenCount,
                ["t1_label_nb_mean_len"] = labelNbLenCount == 0 ? 0 : labelNbLenSum / (double)labelNbLenCount,
                ["t1_alias_en_mean_len"] = aliasEnLenCount == 0 ? 0 : aliasEnLenSum / (double)aliasEnLenCount,
                ["t1_alias_nb_mean_len"] = aliasNbLenCount == 0 ? 0 : aliasNbLenSum / (double)aliasNbLenCount,
            },
            ["distributions"] = new Dictionary<string, object?>
            {
                ["p31_target_fanout"] = FanoutDoc(p31Fan),
                ["p279_target_fanout"] = FanoutDoc(p279Fan),
                ["outgoing_p31_degree"] = DegreeDoc(p31Out),
                ["outgoing_p279_degree"] = DegreeDoc(p279Out),
            },
            ["operational"] = new Dictionary<string, object?>
            {
                ["wall_seconds"] = ev.WallSeconds,
                ["peak_rss_sampled_kb"] = ev.PeakRssSampledKb,
                ["peak_rss_note"] = "sampled managed allocation; authoritative peak from /usr/bin/time -l wrapper",
                ["temp_disk_peak_bytes"] = ev.TempDiskPeakBytes,
                ["temp_disk_peak_note"] = "sampled aggregation.sqlite size during scan",
                ["endpoint_artifact_bytes"] = ev.EndpointFileBytes,
                ["hashed_compressed_bytes"] = ev.HashedBytes,
            },
            ["artifacts"] = new Dictionary<string, object?>
            {
                ["t2_endpoints"] = Path.Combine(opts.WorkDir, "t2-endpoints.bin"),
                ["p31_probe_hints"] = p31Fan.ArtifactPath,
                ["p279_probe_hints"] = p279Fan.ArtifactPath,
            },
        };

        string evPath = Path.Combine(opts.WorkDir, "evidence.json");
        File.WriteAllText(evPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, object?> FanoutDoc(FanoutResult f) => new()
    {
        ["distinct_targets"] = f.Distinct,
        ["min"] = f.Min,
        ["median"] = f.Median,
        ["p90"] = f.P90,
        ["p95"] = f.P95,
        ["p99"] = f.P99,
        ["max"] = f.Max,
    };

    private static Dictionary<string, object?> DegreeDoc(PassALogic.DegreeStats d) => new()
    {
        ["item_count"] = d.ItemCount,
        ["min"] = d.Min,
        ["median"] = d.Median,
        ["p90"] = d.P90,
        ["p95"] = d.P95,
        ["p99"] = d.P99,
        ["max"] = d.Max,
        ["overflow_threshold"] = PassALogic.OverflowDegree,
    };

    public static void WriteState(string workDir, PassAStateKind state)
    {
        Directory.CreateDirectory(workDir);
        string path = Path.Combine(workDir, "state.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { state = state.ToString(), utc = DateTime.UtcNow }));
        File.Move(tmp, path, true);
    }
}
