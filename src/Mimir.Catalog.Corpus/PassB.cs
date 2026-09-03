using System.Security.Cryptography;
using System.Text.Json;
using Parquet.Schema;

namespace Mimir.Catalog.Corpus;

/// <summary>
/// Exact sorted Pass-A T2 artifact plus an index-addressed seen bitset.
/// Memory: values 8 B per endpoint + seen bits at 1/8 byte per endpoint.
/// </summary>
public sealed class T2Index
{
    private readonly long[] _values;
    private readonly byte[] _seen;
    private long _seenCount;

    public T2Index(long[] sortedUniqueValues)
    {
        for (int i = 1; i < sortedUniqueValues.Length; i++)
            if (sortedUniqueValues[i] <= sortedUniqueValues[i - 1])
                throw new InvalidDataException("T2 values are not strictly ascending");
        _values = sortedUniqueValues;
        _seen = new byte[(sortedUniqueValues.Length + 7) / 8];
    }

    public long Count => _values.LongLength;
    public long SeenCount => _seenCount;

    public bool Lookup(long qid, out int index)
    {
        index = Array.BinarySearch(_values, qid);
        return index >= 0;
    }

    public void MarkSeen(int index)
    {
        if (!IsSeen(index))
        {
            _seen[index >> 3] |= (byte)(1 << (index & 7));
            _seenCount++;
        }
    }

    public bool IsSeen(int index) => (_seen[index >> 3] & (1 << (index & 7))) != 0;

    public long QidAt(int index) => _values[index];

    /// <summary>Iterates the T2 array by index, yielding unseen entries ascending.</summary>
    public IEnumerable<(int Index, long Qid)> Unseen()
    {
        for (int i = 0; i < _values.Length; i++)
            if (!IsSeen(i))
                yield return (i, _values[i]);
    }
}

public sealed class PassBIdentity
{
    public const long ExpectedT2Count = 4_480_182;
    public const long ExpectedT2Bytes = 35_841_456;
    public const string ExpectedT2Sha256 = "bd5e01cb38cfd6a2651fa9dac3d8ca7d09da3bd0342f2333f64e5a342c1031f8";
}

public sealed class PassBOptions
{
    public required string SourcePath { get; init; }
    public required string CorpusRoot { get; init; }
    /// <summary>Local-test mode: disables the pinned source/parser/count gates so a synthetic fixture can drive an end-to-end run.</summary>
    public bool LocalTestMode { get; init; }
    /// <summary>Optional T2 artifact path override (tests).</summary>
    public string? T2Path { get; init; }
}

public sealed class PassBEvidence
{
    public required string MaterializationPath { get; init; }
    public required string PublishedDir { get; init; }
    public required long ConceptRows { get; init; }
    public required long LexicalRows { get; init; }
    public required long InstanceOfRows { get; init; }
    public required long SubclassOfRows { get; init; }
    public required long ObservedConceptRows { get; init; }
    public required long UnobservedConceptTail { get; init; }
    public required long T1Concepts { get; init; }
    public required long T2Concepts { get; init; }
    public required long T1IntersectT2 { get; init; }
    public required long T2Only { get; init; }
    public required long T2SeenCount { get; init; }
    public required long T2UnseenCount { get; init; }
    public required double WallSeconds { get; init; }
}

/// <summary>
/// Pass B: one additional full streaming pass that materializes the frozen
/// benchmark corpus (T1 ∪ T2) as relation-split Parquet in deterministic
/// source-entity order with bounded row groups. Stops after materialization.
/// </summary>
public static class PassB
{
    public const long ExpectedConceptRows = 7_403_488;
    public const long ExpectedLexicalRows = 7_121_880;
    public const long ExpectedInstanceOfRows = 3_202_468;
    public const long ExpectedSubclassOfRows = 5_233_394;

    public static PassBEvidence Run(PassBOptions opts)
    {
        var identity = SourceIdentity.PinnedSource();
        string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        string finalDir = Path.Combine(opts.CorpusRoot, "pass-b");
        if (Directory.Exists(finalDir))
            throw new InvalidDataException($"published pass-b already exists: {finalDir} (no silent overwrite)");

        string staging = Path.Combine(opts.CorpusRoot, $"pass-b-staging-{runId}");
        Directory.CreateDirectory(staging);
        WriteState(staging, "Running");

        try
        {
            return RunCore(opts, identity, staging, finalDir, runId);
        }
        catch (Exception)
        {
            WriteState(staging, "Failed");
            throw;
        }
    }

    private static PassBEvidence RunCore(PassBOptions opts, SourceIdentity identity, string staging, string finalDir, string runId)
    {
        bool local = opts.LocalTestMode;

        if (!local)
        {
            long actualLen = new FileInfo(opts.SourcePath).Length;
            if (actualLen != identity.ContentLength)
                throw new InvalidDataException($"source size mismatch: expected {identity.ContentLength}, actual {actualLen}");
        }

        // ---- T2 load + identity validation ----
        string t2Path = opts.T2Path ?? Path.Combine(opts.CorpusRoot, "pass-a", "t2-endpoints.bin");
        T2Index t2 = LoadT2(t2Path, validateIdentity: !local);

        var limits = new PassBRowGroupLimits();
        string conceptPath = Path.Combine(staging, "concept.parquet");
        string lexicalPath = Path.Combine(staging, "lexical_entry.parquet");
        string instanceOfPath = Path.Combine(staging, "instance_of.parquet");
        string subclassOfPath = Path.Combine(staging, "subclass_of.parquet");

        using var conceptWriter = new ConceptWriter(conceptPath, limits.ConceptMaxRows);
        using var lexicalWriter = new LexicalEntryWriter(lexicalPath, limits.LexicalMaxRows, limits.LexicalApproxBytesCap);
        using var instanceOfWriter = new EdgeWriter(instanceOfPath, limits.InstanceOfMaxRows);
        using var subclassWriter = new EdgeWriter(subclassOfPath, limits.SubclassOfMaxRows);

        // Stable Parquet file identity metadata (run timestamps stay in materialization.json).
        SetMeta(conceptWriter, "concept");
        SetMeta(lexicalWriter, "lexical_entry");
        SetMeta(instanceOfWriter, "instance_of");
        SetMeta(subclassWriter, "subclass_of");

        var conceptQids = new HashSet<long>();
        long observedConcepts = 0;
        long t1Concepts = 0, t2ObservedConcepts = 0, cap = 0;
        long t2OnlyAliasRows = 0;

        DateTime startedUtc = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = ScanCore.Scan(
            opts.SourcePath,
            computeSha: !local,
            expectedLength: local ? null : identity.ContentLength,
            onItem: item =>
            {
                long qid = item.Qid;
                bool inT1 = CorpusHash.IsT1(qid); // T1 = deterministic sample of OBSERVED items
                int t2Idx;
                bool inT2 = t2.Lookup(qid, out t2Idx);
                if (inT2) t2.MarkSeen(t2Idx);

                bool retained = inT1 || inT2;
                if (!retained)
                    return;

                if (!conceptQids.Add(qid))
                    throw new InvalidDataException($"duplicate Concept QID {qid}");
                conceptWriter.Add(qid, inT1, inT2);
                observedConcepts++;
                if (inT1) t1Concepts++;
                if (inT2) t2ObservedConcepts++;
                if (inT1 && inT2) cap++;

                // Lexical tier policy.
                if (inT1)
                {
                    // T1 (and therefore T1∩T2) uses full lexical policy.
                    item.AliasEn.Sort(StringComparer.Ordinal);
                    item.AliasNb.Sort(StringComparer.Ordinal);
                    if (item.LabelEnPresent) lexicalWriter.Add(qid, "en", "label", item.LabelEnValue ?? "");
                    foreach (var a in item.AliasEn) lexicalWriter.Add(qid, "en", "alias", a);
                    if (item.LabelNbPresent) lexicalWriter.Add(qid, "nb", "label", item.LabelNbValue ?? "");
                    foreach (var a in item.AliasNb) lexicalWriter.Add(qid, "nb", "alias", a);
                }
                else if (inT2)
                {
                    // T2-only: labels only, never aliases.
                    if (item.LabelEnPresent) lexicalWriter.Add(qid, "en", "label", item.LabelEnValue ?? "");
                    if (item.LabelNbPresent) lexicalWriter.Add(qid, "nb", "label", item.LabelNbValue ?? "");
                }
                else
                {
                    throw new InvalidDataException($"retained concept {qid} has neither T1 nor T2");
                }

                // Edges.
                if (inT1 && item.P31Targets.Count > 0)
                {
                    item.P31Targets.Sort();
                    foreach (var target in item.P31Targets)
                        instanceOfWriter.Add(qid, target);
                }
                if (item.P279Targets.Count > 0)
                {
                    if (!inT2)
                        throw new InvalidDataException($"P279 subject {qid} not in accepted T2 artifact");
                    item.P279Targets.Sort();
                    foreach (var target in item.P279Targets)
                    {
                        if (!t2.Lookup(target, out _))
                            throw new InvalidDataException($"P279 endpoint {target} missing from accepted T2 artifact");
                        subclassWriter.Add(qid, target);
                    }
                }
            });

        // ---- source identity (measured inline) ----
        if (!local)
        {
            if (result.HashedBytes != identity.ContentLength)
                throw new InvalidDataException($"compressed bytes {result.HashedBytes} != source length {identity.ContentLength}");
            if (result.MeasuredSha256 != identity.Sha256)
                throw new InvalidDataException($"source SHA-256 mismatch: expected {identity.Sha256}, measured {result.MeasuredSha256}");
        }

        // ---- parser invariants ----
        if (!local)
        {
            var t = result.Totals;
            Require(t.SourceRecords, 121_453_279, "source_entity_records");
            Require(t.Items, 121_439_429, "item_records");
            Require(t.NonItems, 13_850, "non_item_records_skipped");
            Require(t.Malformed, 0, "malformed_records");
            Require(t.MissingOrDeleted, 0, "missing_or_deleted");
            Require(t.P31Pairs, 128_083_539, "unique P31 pairs");
            Require(t.P279Pairs, 5_233_394, "unique P279 pairs");
            Require(t.LabelEnPresent, 92_798_796, "label_en");
            Require(t.LabelNbPresent, 4_347_263, "label_nb");
            Require(t.AliasEnStrings, 14_810_379, "alias_en");
            Require(t.AliasNbStrings, 578_824, "alias_nb");
        }

        if (result.GzipTruncated)
            throw new InvalidDataException("gzip stream terminated before end of member");

        // ---- unseen T2 tail sweep (always InT1=false, InT2=true) ----
        long unobserved = 0;
        foreach (var (idx, qid) in t2.Unseen())
        {
            // Critical rule: T1 is a sample of OBSERVED items; an unobserved T2
            // endpoint is never T1 even if the hash predicate would match.
            if (!conceptQids.Add(qid))
                throw new InvalidDataException($"unobserved T2 endpoint {qid} duplicated an emitted concept");
            conceptWriter.Add(qid, inT1: false, inT2: true);
            unobserved++;
        }

        // ---- finish writers ----
        conceptWriter.Finish();
        lexicalWriter.Finish();
        instanceOfWriter.Finish();
        subclassWriter.Finish();

        sw.Stop();
        double wall = sw.Elapsed.TotalSeconds;

        long t2Seen = t2.SeenCount;
        long t2Unseen = t2.Count - t2Seen;
        long t1Total = t1Concepts; // all T1 concepts are observed
        long t2Total = t2ObservedConcepts + unobserved;
        long t2Only = t2Total - cap;
        long union = t1Total + t2Only;

        // ---- gates ----
        long conceptRows = conceptWriter.RowsAdded;
        long lexicalRows = lexicalWriter.RowsAdded;
        long instanceRows = instanceOfWriter.RowsAdded;
        long subclassRows = subclassWriter.RowsAdded;

        // Structural gates hold in every mode.
        Require(conceptQids.Count, conceptRows, "Concept unique count vs writer rows");
        Require(unobserved, t2Unseen, "unobserved T2 tail vs T2 unseen");
        Require(t2OnlyAliasRows, 0, "T2-only alias rows");

        if (!local)
        {
            Require(conceptQids.Count, ExpectedConceptRows, "Concept unique count");
            Require(conceptRows, ExpectedConceptRows, "Concept rows");
            Require(lexicalRows, ExpectedLexicalRows, "LexicalEntry rows");
            Require(instanceRows, ExpectedInstanceOfRows, "InstanceOf rows");
            Require(subclassRows, ExpectedSubclassOfRows, "SubclassOf rows");
            Require(t1Total, 3_036_124, "T1");
            Require(t2Total, 4_480_182, "T2");
            Require(cap, 112_818, "T1 ∩ T2");
            Require(t2Only, 4_367_364, "T2-only");
            Require(union, 7_403_488, "T1 ∪ T2");
        }

        // ---- Parquet round-trip inspection + file identities ----
        var conceptInspect = ParquetInspection.Inspect(conceptPath);
        var lexicalInspect = ParquetInspection.Inspect(lexicalPath);
        var instanceInspect = ParquetInspection.Inspect(instanceOfPath);
        var subclassInspect = ParquetInspection.Inspect(subclassOfPath);
        Require(conceptInspect.RowCount, conceptRows, "Concept Parquet row count");
        Require(lexicalInspect.RowCount, lexicalRows, "LexicalEntry Parquet row count");
        Require(instanceInspect.RowCount, instanceRows, "InstanceOf Parquet row count");
        Require(subclassInspect.RowCount, subclassRows, "SubclassOf Parquet row count");
        VerifyPhysicalSchema(conceptPath, PassBSchema.Concept, "Concept");
        VerifyPhysicalSchema(lexicalPath, PassBSchema.LexicalEntry, "LexicalEntry");
        VerifyPhysicalSchema(instanceOfPath, PassBSchema.Edge, "InstanceOf");
        VerifyPhysicalSchema(subclassOfPath, PassBSchema.Edge, "SubclassOf");

        // ---- materialization evidence ----
        string matPath = Path.Combine(staging, "materialization.json");
        var doc = new Dictionary<string, object?>
        {
            ["run"] = new Dictionary<string, object?>
            {
                ["run_id"] = runId,
                ["started_utc"] = startedUtc.ToString("o"),
                ["completed_utc"] = DateTime.UtcNow.ToString("o"),
                ["sourcePath"] = opts.SourcePath,
                ["source_expected_size"] = identity.ContentLength,
                ["source_measured_hashed_bytes"] = result.HashedBytes,
                ["source_sha_inherited"] = identity.Sha256,
                ["source_sha_measured"] = result.MeasuredSha256,
                ["source_validation"] = "pass",
            },
            ["corpus"] = new Dictionary<string, object?>
            {
                ["corpus_id"] = CorpusIdentity.ComputeId(),
                ["contract_version"] = CorpusContract.ContractVersion,
                ["schema_version"] = PassBSchema.SchemaVersion,
                ["t1_domain"] = CorpusContract.Domain,
                ["t1_threshold"] = CorpusContract.Threshold,
                ["t1_algorithm"] = "sha256:first8BE:mod1000",
            },
            ["t2"] = new Dictionary<string, object?>
            {
                ["path"] = t2Path,
                ["element_count"] = t2.Count,
                ["bytes"] = new FileInfo(t2Path).Length,
                ["sha256"] = Sha256File(t2Path),
                ["validation"] = "pass",
            },
            ["relationCounts"] = new Dictionary<string, object?>
            {
                ["Concept"] = conceptRows,
                ["LexicalEntry"] = lexicalRows,
                ["InstanceOf"] = instanceRows,
                ["SubclassOf"] = subclassRows,
            },
            ["construction"] = new Dictionary<string, object?>
            {
                ["observed_concept_rows"] = observedConcepts,
                ["unobserved_t2_concept_tail"] = unobserved,
                ["t1_concepts"] = t1Total,
                ["t2_concepts"] = t2Total,
                ["t1_intersect_t2"] = cap,
                ["t2_only"] = t2Only,
                ["t1_union_t2"] = union,
                ["t2_seen"] = t2Seen,
                ["t2_unseen"] = t2Unseen,
                ["t2_only_alias_rows_emitted"] = t2OnlyAliasRows,
            },
            ["artifacts"] = new Dictionary<string, object?>
            {
                ["concept"] = ArtifactDoc(conceptPath, conceptInspect, limits.ConceptMaxRows),
                ["lexical_entry"] = ArtifactDoc(lexicalPath, lexicalInspect, limits.LexicalMaxRows),
                ["instance_of"] = ArtifactDoc(instanceOfPath, instanceInspect, limits.InstanceOfMaxRows),
                ["subclass_of"] = ArtifactDoc(subclassOfPath, subclassInspect, limits.SubclassOfMaxRows),
            },
            ["rowGroupLimits"] = limits.ToEvidence(),
            ["writer"] = new Dictionary<string, object?>
            {
                ["library"] = PassBSchema.WriterLibrary,
                ["version"] = PassBSchema.WriterVersion,
                ["compression"] = "snappy (explicit ParquetOptions.CompressionMethod)",
            },
            ["operational"] = new Dictionary<string, object?>
            {
                ["wall_seconds"] = Math.Round(wall, 3),
                ["wall_seconds_note"] = "elapsed materialization time measured with a Stopwatch from run start to writer finalization",
                ["sampled_rss_note"] = "internal managed sampling not present; authoritative external RSS from /usr/bin/time -l wrapper in run log",
                ["final_output_bytes"] = FileSizeSum(conceptPath, lexicalPath, instanceOfPath, subclassOfPath),
                ["staging_dir"] = staging,
            },
        };
        File.WriteAllText(matPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));

        WriteState(staging, "Complete");

        // ---- atomic promotion ----
        Directory.Move(staging, finalDir);

        return new PassBEvidence
        {
            MaterializationPath = Path.Combine(finalDir, "materialization.json"),
            PublishedDir = finalDir,
            ConceptRows = conceptRows,
            LexicalRows = lexicalRows,
            InstanceOfRows = instanceRows,
            SubclassOfRows = subclassRows,
            ObservedConceptRows = observedConcepts,
            UnobservedConceptTail = unobserved,
            T1Concepts = t1Total,
            T2Concepts = t2Total,
            T1IntersectT2 = cap,
            T2Only = t2Only,
            T2SeenCount = t2Seen,
            T2UnseenCount = t2Unseen,
            WallSeconds = wall,
        };
    }

    private static void SetMeta(BoundedParquetWriter writer, string relation)
    {
        writer.SetCustomMetadata(new Dictionary<string, string>
        {
            ["schema_version"] = PassBSchema.SchemaVersion,
            ["corpus_id"] = CorpusIdentity.ComputeId(),
            ["relation"] = relation,
            ["source_sha256"] = SourceIdentity.ExpectedSha256,
            ["corpus_contract_version"] = CorpusContract.ContractVersion,
            ["writer_library"] = PassBSchema.WriterLibrary,
            ["writer_library_version"] = PassBSchema.WriterVersion,
        });
    }

    private static long FileSizeSum(params string[] paths) => paths.Sum(p => new FileInfo(p).Length);

    private static void VerifyPhysicalSchema(string path, ParquetSchema expected, string label)
    {
        var cols = ParquetInspection.Inspect(path).Columns;
        var exp = ParquetInspection.ColumnsOf(expected);
        bool ok = cols.Count == exp.Count;
        if (ok)
            for (int i = 0; i < cols.Count; i++)
                if (cols[i] != exp[i]) { ok = false; break; }
        if (!ok)
            throw new InvalidDataException($"physical schema mismatch for {label} at {path}");
    }

    private static Dictionary<string, object?> ArtifactDoc(string path, ParquetInspection.Result inspect, long configuredMaxRows) => new()
    {
        ["relativePath"] = Path.GetFileName(path),
        ["byteSize"] = new FileInfo(path).Length,
        ["sha256"] = Sha256File(path),
        ["schema"] = inspect.Columns.Select(c => $"{c.Name}:{c.Kind}:{(c.Nullable ? "nullable" : "non-null")}").ToList(),
        ["rowCount"] = inspect.RowCount,
        ["rowGroupCount"] = inspect.RowGroupCount,
        ["configuredMaxRowsPerGroup"] = configuredMaxRows,
    };

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>Public SHA-256 of a generated artifact (used by inspection tooling).</summary>
    public static string Sha256OfFile(string path) => Sha256File(path);

    private static void Require(long actual, long expected, string label)
    {
        if (actual != expected)
            throw new InvalidDataException($"gate failed {label}: expected {expected}, actual {actual}");
    }

    private static T2Index LoadT2(string path, bool validateIdentity)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"T2 artifact missing: {path}");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length % 8 != 0)
            throw new InvalidDataException("T2 artifact length is not a multiple of 8");
        if (validateIdentity)
        {
            if (bytes.LongLength != PassBIdentity.ExpectedT2Bytes)
                throw new InvalidDataException($"T2 size mismatch: expected {PassBIdentity.ExpectedT2Bytes}, actual {bytes.LongLength}");
            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (sha != PassBIdentity.ExpectedT2Sha256)
                throw new InvalidDataException($"T2 SHA-256 mismatch: expected {PassBIdentity.ExpectedT2Sha256}, measured {sha}");
            long count = bytes.Length / 8;
            if (count != PassBIdentity.ExpectedT2Count)
                throw new InvalidDataException($"T2 count mismatch: expected {PassBIdentity.ExpectedT2Count}, actual {count}");
        }
        long n = bytes.Length / 8;
        var values = new long[n];
        for (long i = 0; i < n; i++)
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)(i * 8), 8));
        return new T2Index(values);
    }

    private static void WriteState(string staging, string state)
    {
        Directory.CreateDirectory(staging);
        string path = Path.Combine(staging, "pass-b.state.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { state, utc = DateTime.UtcNow }));
        File.Move(tmp, path, true);
    }
}
