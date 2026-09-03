using System.Text.Json;

namespace Mimir.Catalog.Workload;

/// <summary>
/// Publication discipline for authoritative generated workloads: write under a
/// run-id staging directory, then atomically promote to workload-v1/ only for a
/// fully generated, correctness-validated output. Never silently overwrites an
/// existing published workload. Run id/timestamps are operational only and
/// never influence the workload identity.
/// </summary>
public sealed class WorkloadPublisher
{
    private readonly string _benchmarkRoot;
    private readonly string _corpusId;
    private readonly string _workloadId;

    public sealed class Result
    {
        public bool Ok;
        public List<string> Reasons = new();
        public Dictionary<string, string> FileSha256 = new();
        public string? PublishedDir;
    }

    public WorkloadPublisher(string benchmarkRoot, string corpusId, string workloadId)
    {
        _benchmarkRoot = benchmarkRoot;
        _corpusId = corpusId;
        _workloadId = workloadId;
    }

    public Result Publish(
        WorkloadContractV1 c,
        byte[] servingLines,
        byte[] graphLines,
        byte[] expectedLines,
        byte[] analyticalLines,
        IReadOnlyDictionary<string, long> poolCardinalities,
        IReadOnlyDictionary<string, object> continuity,
        long measuredServingCount,
        long measuredG1Count,
        int g2BatchCount,
        int g1Candidates, int g1Rejected, long g1MaxVisited,
        int g2Candidates, int g2Rejected, long g2MaxVisited,
        IReadOnlyDictionary<string, string> provenance)
    {
        var res = new Result();
        string finalDir = Path.Combine(_benchmarkRoot, "workload-v1");
        if (Directory.Exists(finalDir))
        {
            res.Reasons.Add($"published workload already exists: {finalDir}");
            return res;
        }
        Directory.CreateDirectory(_benchmarkRoot);
        string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        string staging = Path.Combine(_benchmarkRoot, $"workload-v1-staging-{runId}");
        Directory.CreateDirectory(staging);

        var content = new (string Name, byte[] Bytes)[]
        {
            ("serving-probes.jsonl", servingLines),
            ("graph-probes.jsonl", graphLines),
            ("expected-results.jsonl", expectedLines),
            ("analytical-expected.jsonl", analyticalLines),
        };
        try
        {
            WriteState(staging, "Running", runId);
            foreach (var f in content) File.WriteAllBytes(Path.Combine(staging, f.Name), f.Bytes);

            // Manifest (deterministic; no timestamps/paths inside).
            var hashes = new Dictionary<string, string>();
            foreach (var f in content) hashes[f.Name] = Canon.Sha256Hex(f.Bytes);
            byte[] manifest = BuildManifest(c, hashes, poolCardinalities, continuity,
                measuredServingCount, measuredG1Count, g2BatchCount,
                g1Candidates, g1Rejected, g1MaxVisited,
                g2Candidates, g2Rejected, g2MaxVisited,
                provenance);
            File.WriteAllBytes(Path.Combine(staging, "manifest.json"), manifest);
            hashes["manifest.json"] = Canon.Sha256Hex(manifest);

            WriteState(staging, "Complete", runId);

            try
            {
                Directory.Move(staging, finalDir);
            }
            catch (IOException)
            {
                // Target appeared between check and move (or cross-volume).
                WriteState(staging, "Hold", runId);
                res.Reasons.Add($"could not promote (target exists): {finalDir}");
                return res;
            }

            res.Ok = true;
            res.PublishedDir = finalDir;
            foreach (var kv in hashes) res.FileSha256[kv.Key] = kv.Value;
            return res;
        }
        catch (Exception ex)
        {
            WriteState(staging, "Failed", runId);
            res.Reasons.Add($"publication failed: {ex.Message}");
            return res;
        }
    }

    private void WriteState(string dir, string state, string runId)
    {
        byte[] b = JsonLines.SingleObject(w =>
        {
            w.WriteString("state", state);
            w.WriteString("run_id", runId);
            w.WriteString("workload_id", _workloadId);
            w.WriteString("corpus_id", _corpusId);
            w.WriteString("utc", DateTime.UtcNow.ToString("O"));
        });
        File.WriteAllBytes(Path.Combine(dir, "workload.state.json"), b);
    }

    private byte[] BuildManifest(
        WorkloadContractV1 c,
        Dictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, long> poolCardinalities,
        IReadOnlyDictionary<string, object> continuity,
        long measuredServing, long measuredG1, int g2,
        int g1Candidates, int g1Rejected, long g1MaxVisited,
        int g2Candidates, int g2Rejected, long g2MaxVisited,
        IReadOnlyDictionary<string, string> provenance)
    {
        return JsonLines.SingleObject(w =>
        {
            w.WriteString("schema", "mimir-catalog-workload-manifest-v1");
            w.WriteString("workload_id", _workloadId);
            w.WriteString("corpus_id", _corpusId);
            w.WriteString("lexical_key_semantics", c.LexicalKeySemantics);
            w.WriteString("percentile_algorithm", c.PercentileAlgorithm);
            w.WriteNumber("workload_contract_version", c.WorkloadContractVersion);
            w.WriteNumber("generator_version", c.GenVersion);
            w.WriteNumber("canonical_encoding_version", c.EncVersion);
            w.WriteNumber("multiset_fold_version", c.FoldVersion);
            w.WriteString("ordering_algorithm", c.OrderingAlgorithm);
            w.WriteString("warmup_semantics", c.FullPassWarmup ? "one-full-untimed-pass" : "none");
            w.WriteNumber("max_depth", c.MaxDepth);
            w.WriteNumber("visited_node_guard", c.VisitedNodeGuard);
            w.WriteNumber("g2_batch_concepts", c.G2BatchConcepts);

            w.WritePropertyName("strata");
            w.WriteStartArray();
            foreach (var s in c.Strata)
            {
                w.WriteStartObject();
                w.WriteString("operation", s.Operation);
                w.WriteString("stratum", s.Stratum);
                w.WriteString("selection_mode", s.SelectionMode);
                w.WriteNumber("measured_count", s.MeasuredCount);
                if (s.FanoutMin != null) w.WriteNumber("fanout_min", s.FanoutMin.Value);
                if (s.FanoutMax != null) w.WriteNumber("fanout_max", s.FanoutMax.Value);
                if (s.ExpectedEligibleCount != null) w.WriteNumber("expected_eligible_count", s.ExpectedEligibleCount.Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("correctness_only");
            w.WriteStartArray();
            foreach (var s in c.CorrectnessOnly)
            {
                w.WriteStartObject();
                w.WriteString("operation", s.Operation);
                w.WriteString("stratum", s.Stratum);
                w.WriteString("selection_mode", s.SelectionMode);
                w.WriteNumber("count", s.MeasuredCount);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("g2_strata");
            w.WriteStartArray();
            foreach (var s in c.G2Strata)
            {
                w.WriteStartObject();
                w.WriteString("stratum", s.Stratum);
                w.WriteNumber("count", s.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteNumber("measured_serving_probes", measuredServing);
            w.WriteNumber("measured_g1_probes", measuredG1);
            w.WriteNumber("g2_batch_size", g2);

            w.WritePropertyName("pool_cardinalities");
            w.WriteStartObject();
            foreach (var key in poolCardinalities.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                w.WritePropertyName(key);
                w.WriteNumberValue(poolCardinalities[key]);
            }
            w.WriteEndObject();

            w.WritePropertyName("continuity");
            w.WriteStartObject();
            foreach (var key in continuity.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                w.WritePropertyName(key);
                var v = continuity[key];
                switch (v)
                {
                    case long l: w.WriteNumberValue(l); break;
                    case int i: w.WriteNumberValue(i); break;
                    default: w.WriteNumberValue(Convert.ToInt64(v)); break;
                }
            }
            w.WriteEndObject();

            w.WritePropertyName("g1_diagnostics");
            w.WriteStartObject();
            w.WriteNumber("candidates_considered", g1Candidates);
            w.WriteNumber("rejected_guard", g1Rejected);
            w.WriteNumber("accepted", measuredG1);
            w.WriteNumber("max_visited", g1MaxVisited);
            w.WriteEndObject();

            w.WritePropertyName("g2_diagnostics");
            w.WriteStartObject();
            w.WriteNumber("candidates_considered", g2Candidates);
            w.WriteNumber("rejected_guard", g2Rejected);
            w.WriteNumber("accepted", g2);
            w.WriteNumber("max_visited", g2MaxVisited);
            w.WriteEndObject();

            w.WritePropertyName("provenance");
            w.WriteStartObject();
            foreach (var kv in provenance.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WritePropertyName(kv.Key);
                w.WriteStringValue(kv.Value);
            }
            w.WriteEndObject();

            w.WritePropertyName("files");
            w.WriteStartObject();
            foreach (var f in fileHashes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WritePropertyName(f.Key);
                w.WriteStringValue(f.Value);
            }
            w.WriteEndObject();
        });
    }
}

internal static class JsonLines
{
    private static readonly JsonWriterOptions Jwo = new() { SkipValidation = false };

    public static byte[] SingleObject(Action<Utf8JsonWriter> write)
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
}
