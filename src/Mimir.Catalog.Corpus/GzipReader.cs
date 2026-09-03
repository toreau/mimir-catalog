using System.IO.Compression;
using System.Security.Cryptography;

namespace Mimir.Catalog.Corpus;

/// <summary>
/// Stream wrapper that SHA-256 hashes and counts every compressed byte read
/// from the inner stream. Used to measure the fresh source digest inline while
/// the same source stream feeds the Pass-A gzip scan (no separate 155 GB pass).
/// </summary>
public sealed class HashCountingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public HashCountingStream(Stream inner) => _inner = inner;

    /// <summary>Total raw compressed bytes consumed from the inner stream.</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _sha.AppendData(buffer, offset, n);
            BytesRead += n;
        }
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = _inner.Read(buffer);
        if (n > 0)
        {
            _sha.AppendData(buffer[..n]);
            BytesRead += n;
        }
        return n;
    }

    public string Sha256Hex() => Convert.ToHexStringLower(_sha.GetHashAndReset());

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sha.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Decompressed byte line reader over a gzip stream. Mirrors the Phase-0
/// incremental gzip line iterator: lines are split on '\n', and a trailing
/// partial line (truncated member / prefix boundary) is yielded once at EOF.
/// Truncated gzip data is tolerated here; the caller decides policy via
/// <see cref="Truncated"/>.
/// </summary>
public sealed class GzipByteLineReader : IDisposable
{
    private const long MaxLineBytes = 1L << 30; // 1 GiB safety cap for pathological single lines
    private readonly GZipStream _gz;
    private byte[] _buf;
    private int _start;
    private int _len;
    private bool _done;
    private bool _emittedTail;
    private bool _truncated;

    public GzipByteLineReader(Stream compressed)
    {
        _gz = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        _buf = new byte[1 << 20];
    }

    public bool Truncated => _truncated;

    /// <summary>Gets the next line (without the trailing newline). Returns false at end of stream.</summary>
    public bool TryReadLine(out byte[] line)
    {
        line = Array.Empty<byte>();
        while (true)
        {
            int nl = IndexOfNewline();
            if (nl >= 0)
            {
                int lineLen = nl - _start;
                line = new byte[lineLen];
                if (lineLen > 0) Buffer.BlockCopy(_buf, _start, line, 0, lineLen);
                _start = nl + 1;
                return true;
            }

            if (_done)
            {
                int remaining = _len - _start;
                if (remaining > 0 && !_emittedTail)
                {
                    line = new byte[remaining];
                    Buffer.BlockCopy(_buf, _start, line, 0, remaining);
                    _emittedTail = true;
                    return true;
                }
                return false;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _len - _start);
                _len -= _start;
                _start = 0;
            }

            if (_len == _buf.Length)
            {
                if (_buf.LongLength * 2L > MaxLineBytes)
                    throw new InvalidDataException("single decompressed line exceeds the safety cap");
                Array.Resize(ref _buf, checked((int)(_buf.LongLength * 2L)));
            }

            int n;
            try
            {
                n = _gz.Read(_buf, _len, _buf.Length - _len);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
            {
                _truncated = true;
                _done = true;
                continue;
            }

            if (n == 0)
            {
                _done = true;
                continue;
            }
            _len += n;
        }
    }

    private int IndexOfNewline()
    {
        for (int i = _start; i < _len; i++)
            if (_buf[i] == (byte)'\n')
                return i;
        return -1;
    }

    public void Dispose() => _gz.Dispose();
}
