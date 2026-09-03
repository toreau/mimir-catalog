using System.Globalization;

namespace Mimir.Catalog.BenchmarkCli.Resource;

public sealed class RssParseException : Exception
{
    public RssParseException(string message) : base(message) { }
}

/// <summary>
/// Strict Darwin /usr/bin/time resource parser. Accepts exactly one line:
/// &lt;unsigned decimal Int64&gt; whitespace+ "maximum resident set size"
/// (whitespace allowed around and between). Value is raw bytes; no ×1024.
/// </summary>
public static class MacOsTimeRssParser
{
    private const string Label = "maximum resident set size";

    public static bool TryParse(string text, out long bytes, out string? error)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "resource output is empty";
            return false;
        }

        long? found = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            int idx = line.IndexOf(Label, StringComparison.Ordinal);
            if (idx < 0) continue;
            if (idx + Label.Length != line.Length) continue; // trailing words after label rejected
            if (idx > 0 && !char.IsWhiteSpace(line[idx - 1])) continue;

            string numberToken = line[..idx].Trim();
            if (numberToken.Length == 0)
            {
                error = $"malformed RSS line: missing numeric token before '{Label}'";
                return false;
            }

            // NumberStyles.None: unsigned decimal digits only (no sign, separator, decimal, exponent).
            if (!long.TryParse(numberToken, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                error = $"invalid RSS numeric token '{numberToken}'";
                return false;
            }

            if (found is not null)
            {
                error = "duplicate 'maximum resident set size' lines";
                return false;
            }
            found = value;
        }

        if (found is null)
        {
            error = $"no '{Label}' field found";
            return false;
        }

        bytes = found.Value;
        error = null;
        return true;
    }

    public static long ParseFile(string path)
    {
        if (!File.Exists(path)) throw new RssParseException($"resource output file missing: {path}");
        if (!TryParse(File.ReadAllText(path), out long bytes, out string? error))
            throw new RssParseException(error ?? "resource parse failed");
        return bytes;
    }
}
