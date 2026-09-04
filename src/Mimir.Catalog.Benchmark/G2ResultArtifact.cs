using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Deterministic JSONL writer for G2 raw timed results. UTF-8 no BOM, compact
/// fixed property order, LF line terminator, no trailing non-JSON. Create-new
/// only; never overwrites. Normal timed run: PerInput records (item 0..N-1)
/// then one Batch record. Zero records produce a valid empty file.
/// </summary>
public static class G2ResultArtifact
{
    public static void WriteCreateNew(
        string path,
        IReadOnlyList<G2TimedPerInputResult> perInput,
        G2TimedBatchResult? batch)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        foreach (var r in perInput)
        {
            byte[] line = SerializePerInput(r);
            fs.Write(line, 0, line.Length);
            fs.WriteByte(0x0A);
        }
        if (batch is null) return;
        byte[] batchLine = SerializeBatch(batch);
        fs.Write(batchLine, 0, batchLine.Length);
        fs.WriteByte(0x0A);
    }

    private static byte[] SerializePerInput(G2TimedPerInputResult r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("kind", "per-input");
            w.WriteString("operation", "G2");
            w.WriteNumber("sequence", 500);
            w.WriteNumber("item", r.Item);
            w.WriteNumber("qid", r.Qid);
            w.WriteString("source_stratum", r.SourceStratum);
            w.WriteString("correctness_status", r.CorrectnessStatus);
            if (r.ActualCardinality is { } card) w.WriteNumber("actual_cardinality", card);
            if (r.ActualDigest is not null) w.WriteString("actual_digest", r.ActualDigest);
            if (r.Error is not null) w.WriteString("error", r.Error);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static byte[] SerializeBatch(G2TimedBatchResult b)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("kind", "batch");
            w.WriteString("operation", "G2");
            w.WriteNumber("sequence", 500);
            w.WriteNumber("wall_seconds", b.WallSeconds);
            w.WriteString("correctness_status", b.CorrectnessStatus);
            if (b.ActualCardinality is { } card) w.WriteNumber("actual_cardinality", card);
            if (b.ActualDigest is not null) w.WriteString("actual_digest", b.ActualDigest);
            if (b.Error is not null) w.WriteString("error", b.Error);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
