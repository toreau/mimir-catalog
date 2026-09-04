using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Deterministic JSONL writer for G1 raw timed samples. UTF-8 no BOM, compact
/// fixed property order, LF line terminator, no trailing non-JSON. Create-new
/// only; never overwrites. Zero samples produce a valid empty file.
/// </summary>
public static class G1SampleArtifact
{
    public static void WriteCreateNew(string path, IReadOnlyList<G1TimedSample> samples)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        foreach (var sample in samples)
        {
            byte[] line = SerializeLine(sample);
            fs.Write(line, 0, line.Length);
            fs.WriteByte(0x0A);
        }
    }

    private static byte[] SerializeLine(G1TimedSample sample)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("operation", sample.Operation);
            w.WriteNumber("sequence", sample.Sequence);
            w.WriteString("stratum", sample.Stratum);
            w.WriteNumber("wall_seconds", sample.WallSeconds);
            w.WriteString("correctness_status", sample.CorrectnessStatus);
            if (sample.ActualCardinality is { } card) w.WriteNumber("actual_cardinality", card);
            if (sample.ActualVisited is { } visited) w.WriteNumber("actual_visited", visited);
            if (sample.ActualDigest is not null) w.WriteString("actual_digest", sample.ActualDigest);
            if (sample.Error is not null) w.WriteString("error", sample.Error);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
