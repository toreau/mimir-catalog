using System.Text;
using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Corpus;

/// <summary>
/// Frozen Pass-B relation schemas. Benchmark-corpus interchange only; never a
/// production serving schema.
/// </summary>
public static class PassBSchema
{
    public const string SchemaVersion = "1";
    public const string WriterLibrary = "Parquet.Net";
    public const string WriterVersion = "6.1.0";

    public static readonly ParquetSchema Concept = new(
        new DataField<long>("Qid", nullable: false),
        new DataField<bool>("InT1", nullable: false),
        new DataField<bool>("InT2", nullable: false));

    public static readonly ParquetSchema LexicalEntry = new(
        new DataField<long>("Qid", nullable: false),
        new DataField<string>("Lang", nullable: false),
        new DataField<string>("LexKind", nullable: false),
        new DataField<string>("Value", nullable: false));

    public static readonly ParquetSchema Edge = new(
        new DataField<long>("SubjectQid", nullable: false),
        new DataField<long>("TargetQid", nullable: false));
}

/// <summary>
/// Documented, persisted row-group limits for bounded-memory writing.
/// Conservative interchange limits; not tuned for any storage candidate.
/// </summary>
public sealed class PassBRowGroupLimits
{
    public int ConceptMaxRows = 500_000;
    public int InstanceOfMaxRows = 1_000_000;
    public int SubclassOfMaxRows = 1_000_000;
    public int LexicalMaxRows = 200_000;
    public long LexicalApproxBytesCap = 64L * 1024 * 1024;

    public Dictionary<string, object?> ToEvidence() => new()
    {
        ["conceptMaxRows"] = ConceptMaxRows,
        ["instanceOfMaxRows"] = InstanceOfMaxRows,
        ["subclassOfMaxRows"] = SubclassOfMaxRows,
        ["lexicalMaxRows"] = LexicalMaxRows,
        ["lexicalApproxBytesCap"] = LexicalApproxBytesCap,
        ["approxByteNote"] =
            "lexical byte cap counts actual UTF-8 bytes of the raw Value column only; QID/Lang/LexKind, object/list " +
            "and Parquet-native buffer overhead are not included. An oversized single value may form a one-row row group.",
    };
}

internal static class Sync
{
    public static T Await<T>(Task<T> t) => t.GetAwaiter().GetResult();
    public static void Await(Task t) => t.GetAwaiter().GetResult();
    public static T Await<T>(ValueTask<T> t) => t.AsTask().GetAwaiter().GetResult();
    public static void Await(ValueTask t) => t.AsTask().GetAwaiter().GetResult();
}

/// <summary>
/// Bounded Parquet writer base: buffers one row group, flushes on configured
/// limits, reuses buffers. Never accumulates an entire relation in memory.
/// Finish() flushes the tail and writes the file footer.
/// </summary>
public abstract class BoundedParquetWriter : IDisposable
{
    private static readonly ParquetOptions WriterOptions = new()
    {
        CompressionMethod = CompressionMethod.Snappy,
    };

    private readonly ParquetWriter _writer;
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _finished;

    protected BoundedParquetWriter(string path, ParquetSchema schema)
    {
        _path = path;
        _stream = File.Create(path);
        _writer = ParquetWriter.CreateAsync(schema, _stream, WriterOptions, append: false, default).GetAwaiter().GetResult();
    }

    public void SetCustomMetadata(IDictionary<string, string> meta) =>
        _writer.CustomMetadata = meta as IReadOnlyDictionary<string, string> ?? new Dictionary<string, string>(meta);

    public long RowsAdded { get; protected set; }
    public int RowGroupCount { get; protected set; }
    public string Path => _path;

    protected abstract int RowsInBuffer { get; }
    protected virtual bool ShouldFlush => false;
    protected abstract void FlushRowGroup();

    protected void FlushIfNeeded()
    {
        if (ShouldFlush) FlushRowGroup();
    }

    protected void FlushRowGroupCore(Action<ParquetRowGroupWriter> write)
    {
        RowGroupCount++;
        using var rg = _writer.CreateRowGroup();
        write(rg);
    }

    protected static void WriteLong(ParquetRowGroupWriter rg, DataField field, long[] values)
        => Sync.Await(rg.WriteAsync<long>(field, new ReadOnlyMemory<long>(values), repetitionLevels: null, customMetadata: null, cancellationToken: default));

    protected static void WriteBool(ParquetRowGroupWriter rg, DataField field, bool[] values)
        => Sync.Await(rg.WriteAsync<bool>(field, new ReadOnlyMemory<bool>(values), repetitionLevels: null, customMetadata: null, cancellationToken: default));

    protected static void WriteStrings(ParquetRowGroupWriter rg, DataField field, string[] values)
        => Sync.Await(rg.WriteAsync(field, (IReadOnlyCollection<string>)values, repetitionLevels: null));

    /// <summary>Flushes any remaining buffered rows and finalizes the file.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            if (RowsInBuffer > 0) FlushRowGroup();
            Sync.Await(_writer.DisposeAsync());
        }
        finally
        {
            _stream.Dispose();
        }
    }

    public void Dispose() => Finish();
}

/// <summary>Concept(Qid, InT1, InT2).</summary>
public sealed class ConceptWriter : BoundedParquetWriter
{
    private readonly int _maxRows;
    private readonly List<long> _q = new();
    private readonly List<bool> _t1 = new();
    private readonly List<bool> _t2 = new();

    public ConceptWriter(string path, int maxRows) : base(path, PassBSchema.Concept) => _maxRows = maxRows;

    public void Add(long qid, bool inT1, bool inT2)
    {
        _q.Add(qid);
        _t1.Add(inT1);
        _t2.Add(inT2);
        RowsAdded++;
        FlushIfNeeded();
    }

    protected override int RowsInBuffer => _q.Count;
    protected override bool ShouldFlush => _q.Count >= _maxRows;

    protected override void FlushRowGroup()
    {
        FlushRowGroupCore(rg =>
        {
            WriteLong(rg, PassBSchema.Concept.DataFields[0], _q.ToArray());
            WriteBool(rg, PassBSchema.Concept.DataFields[1], _t1.ToArray());
            WriteBool(rg, PassBSchema.Concept.DataFields[2], _t2.ToArray());
        });
        _q.Clear();
        _t1.Clear();
        _t2.Clear();
    }
}

/// <summary>
/// LexicalEntry(Qid, Lang, LexKind, Value). The byte cap measures actual UTF-8
/// bytes of the raw Value column. A non-empty buffer is flushed before adding a
/// row that would exceed the cap; a single oversized value may form a one-row
/// row group but is never rejected.
/// </summary>
public sealed class LexicalEntryWriter : BoundedParquetWriter
{
    private readonly int _maxRows;
    private readonly long _byteCap;
    private readonly List<long> _q = new();
    private readonly List<string> _lang = new();
    private readonly List<string> _kind = new();
    private readonly List<string> _value = new();
    private long _approxBytes;

    public LexicalEntryWriter(string path, int maxRows, long byteCap) : base(path, PassBSchema.LexicalEntry)
    {
        _maxRows = maxRows;
        _byteCap = byteCap;
    }

    public void Add(long qid, string lang, string kind, string value)
    {
        int bytes = Encoding.UTF8.GetByteCount(value);
        if (_q.Count > 0 && (_q.Count >= _maxRows || _approxBytes + bytes > _byteCap))
            FlushRowGroup();

        _q.Add(qid);
        _lang.Add(lang);
        _kind.Add(kind);
        _value.Add(value);
        _approxBytes += bytes;
        RowsAdded++;

        if (_q.Count >= _maxRows || _approxBytes >= _byteCap)
            FlushRowGroup();
    }

    protected override int RowsInBuffer => _q.Count;

    protected override void FlushRowGroup()
    {
        FlushRowGroupCore(rg =>
        {
            WriteLong(rg, PassBSchema.LexicalEntry.DataFields[0], _q.ToArray());
            WriteStrings(rg, PassBSchema.LexicalEntry.DataFields[1], _lang.ToArray());
            WriteStrings(rg, PassBSchema.LexicalEntry.DataFields[2], _kind.ToArray());
            WriteStrings(rg, PassBSchema.LexicalEntry.DataFields[3], _value.ToArray());
        });
        _q.Clear();
        _lang.Clear();
        _kind.Clear();
        _value.Clear();
        _approxBytes = 0;
    }
}

/// <summary>InstanceOf / SubclassOf edge writer.</summary>
public sealed class EdgeWriter : BoundedParquetWriter
{
    private readonly int _maxRows;
    private readonly List<long> _s = new();
    private readonly List<long> _t = new();

    public EdgeWriter(string path, int maxRows) : base(path, PassBSchema.Edge) => _maxRows = maxRows;

    public void Add(long subject, long target)
    {
        _s.Add(subject);
        _t.Add(target);
        RowsAdded++;
        FlushIfNeeded();
    }

    protected override int RowsInBuffer => _s.Count;
    protected override bool ShouldFlush => _s.Count >= _maxRows;

    protected override void FlushRowGroup()
    {
        FlushRowGroupCore(rg =>
        {
            WriteLong(rg, PassBSchema.Edge.DataFields[0], _s.ToArray());
            WriteLong(rg, PassBSchema.Edge.DataFields[1], _t.ToArray());
        });
        _s.Clear();
        _t.Clear();
    }
}

/// <summary>
/// Physical Parquet schema inspection: field names, field order, logical/CLR
/// types, nullability, row count and row-group count, read back from the
/// opened artifact (not from the static schema declaration).
/// </summary>
public static class ParquetInspection
{
    public sealed record Column(string Name, string Kind, bool Nullable);

    public sealed record Result(
        string Path,
        long RowCount,
        int RowGroupCount,
        IReadOnlyList<Column> Columns);

    public static IReadOnlyList<string> FieldNames(Result r) => r.Columns.Select(c => c.Name).ToList();

    public static Result Inspect(string path)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            long rows = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                rows += rg.RowCount;
            }
            return new Result(path, rows, reader.RowGroupCount, ColumnsOf(reader.Schema));
        }
        finally
        {
            Sync.Await(reader.DisposeAsync());
        }
    }

    /// <summary>Derives the physical column descriptors of a Parquet schema.</summary>
    public static IReadOnlyList<Column> ColumnsOf(ParquetSchema schema) =>
        schema.DataFields.Select(f =>
        {
            var df = (DataField)f;
            return new Column(df.Name, KindOf(df.ClrType), df.IsNullable);
        }).ToList();

    private static string KindOf(Type clr)
    {
        if (clr == typeof(long)) return "INT64";
        if (clr == typeof(bool)) return "BOOL";
        if (clr == typeof(string)) return "UTF8";
        return clr.Name;
    }
}

/// <summary>Typed column read-back for round-trip tests.</summary>
public static class ParquetRead
{
    public static long[] ReadLongs(string path, int column)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var field = reader.Schema.DataFields[column];
            long total = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                total += rg.RowCount;
            }
            var result = new long[total];
            long at = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                var mem = new Memory<long>(result, (int)at, (int)rg.RowCount);
                Sync.Await(rg.ReadAsync<long>(field, mem, repetitionLevels: null, cancellationToken: default));
                at += rg.RowCount;
            }
            return result;
        }
        finally
        {
            Sync.Await(reader.DisposeAsync());
        }
    }

    public static bool[] ReadBools(string path, int column)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var field = reader.Schema.DataFields[column];
            long total = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                total += rg.RowCount;
            }
            var result = new bool[total];
            long at = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                var mem = new Memory<bool>(result, (int)at, (int)rg.RowCount);
                Sync.Await(rg.ReadAsync<bool>(field, mem, repetitionLevels: null, cancellationToken: default));
                at += rg.RowCount;
            }
            return result;
        }
        finally
        {
            Sync.Await(reader.DisposeAsync());
        }
    }

    public static string[] ReadStrings(string path, int column)
    {
        var reader = ParquetReader.CreateAsync(path).GetAwaiter().GetResult();
        try
        {
            var field = reader.Schema.DataFields[column];
            long total = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                total += rg.RowCount;
            }
            var result = new string[total];
            long at = 0;
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rg = reader.OpenRowGroupReader(i);
                var mem = new Memory<string>(result, (int)at, (int)rg.RowCount);
                Sync.Await(rg.ReadAsync(field, mem, repetitionLevels: null, cancellationToken: default));
                at += rg.RowCount;
            }
            return result;
        }
        finally
        {
            Sync.Await(reader.DisposeAsync());
        }
    }
}
