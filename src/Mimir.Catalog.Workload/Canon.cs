using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Mimir.Catalog.Workload;

/// <summary>
/// CanonicalV1 encoding and digest primitives for the workload contract.
/// Deterministic, culture invariant, platform independent (no locale, no
/// newlines, no path/timestamp material). Numeric fields are big-endian.
/// </summary>
public static class Canon
{
    /// <summary>Versioned canonical encoding marker.</summary>
    public const int CanonicalEncodingVersion = 1;

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] Sha256Bytes(byte[] data) => SHA256.HashData(data);

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string utf8) => Sha256Hex(Utf8(utf8));

    public static string Sha256Hex(byte[] prefix, byte[] data)
    {
        var buffer = new byte[prefix.Length + data.Length];
        prefix.CopyTo(buffer, 0);
        data.CopyTo(buffer, prefix.Length);
        return Sha256Hex(buffer);
    }

    /// <summary>Full 256-bit SHA-256 image as two big-endian UInt128 words.</summary>
    public readonly record struct Hash256(UInt128 Hi, UInt128 Lo) : IComparable<Hash256>
    {
        public static Hash256 Of(byte[] data)
        {
            byte[] h = Sha256Bytes(data);
            return new Hash256(
                BinaryPrimitives.ReadUInt128BigEndian(h),
                BinaryPrimitives.ReadUInt128BigEndian(h.AsSpan(16)));
        }

        public int CompareTo(Hash256 other)
        {
            int c = Hi.CompareTo(other.Hi);
            return c != 0 ? c : Lo.CompareTo(other.Lo);
        }

        public string Hex() => Hi.ToString("x32", CultureInfo.InvariantCulture) + Lo.ToString("x32", CultureInfo.InvariantCulture);
    }

    /// <summary>Incremental canonical byte builder (fixed field structure).</summary>
    public sealed class Builder
    {
        private readonly MemoryStream _ms = new();

        public Builder AddString(string s)
        {
            byte[] b = Utf8(s);
            WriteLen(b.Length);
            _ms.Write(b);
            return this;
        }

        public Builder AddLong(long v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            _ms.Write(b);
            return this;
        }

        public Builder AddUInt64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, v);
            _ms.Write(b);
            return this;
        }

        public Builder AddByte(byte v)
        {
            _ms.WriteByte(v);
            return this;
        }

        public Builder AddRaw(byte[] bytes)
        {
            WriteLen(bytes.Length);
            _ms.Write(bytes);
            return this;
        }

        private void WriteLen(int len)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, len);
            _ms.Write(b);
        }

        public byte[] ToArray() => _ms.ToArray();

        public string ToSha256Hex() => Sha256Hex(ToArray());
    }

    /// <summary>
    /// Deterministic selector bytes for SHA-256 ranking:
    /// UTF8(domain 0x1F operation 0x1F stratum 0x1F candidateIdentity).
    /// </summary>
    public static byte[] SelectorBytes(string domain, string operation, string stratum, string candidateIdentity)
    {
        var b = new StringBuilder();
        b.Append(domain).Append('\u001f').Append(operation).Append('\u001f').Append(stratum).Append('\u001f').Append(candidateIdentity);
        return Utf8(b.ToString());
    }

    /// <summary>
    /// Unambiguous canonical identity for a lexical (Lang, Value) candidate:
    /// 4-byte BE lang byte length + utf8 lang + 4-byte BE value length + utf8 value,
    /// rendered as base64 so it can sit in the 0x1F-joined selector string.
    /// </summary>
    public static string LexicalIdentity(string lang, string value)
    {
        byte[] lb = Utf8(lang);
        byte[] vb = Utf8(value);
        var b = new byte[8 + lb.Length + vb.Length];
        BinaryPrimitives.WriteInt32BigEndian(b, lb.Length);
        lb.CopyTo(b, 4);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4 + lb.Length, 4), vb.Length);
        vb.CopyTo(b, 8 + lb.Length);
        return Convert.ToBase64String(b);
    }
}
