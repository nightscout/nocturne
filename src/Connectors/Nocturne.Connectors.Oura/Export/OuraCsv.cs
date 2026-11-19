using System.Text;

namespace Nocturne.Connectors.Oura.Export;

/// <summary>
/// Reads the semicolon-separated files of an Oura data export. Oura's writer is not a strict CSV
/// writer, so the reader accepts what the export is known to contain:
/// <list type="bullet">
/// <item>a field is quoted only when it starts with a quote; a quote inside an unquoted field is
/// literal, which is how unquoted JSON columns arrive;</item>
/// <item>a whole record wrapped in one pair of quotes is unwrapped and split again;</item>
/// <item>a record shorter than the header lacks its leading <c>id</c> when the header starts with
/// one and the record does not (Oura's ids are UUIDs), and its trailing columns otherwise.</item>
/// </list>
/// </summary>
internal static class OuraCsv
{
    private const char Separator = ';';

    /// <summary>A record longer than this is not an export row; it bounds memory on a hostile file.</summary>
    internal const int MaxRecordChars = 8 * 1024 * 1024;

    public static IEnumerable<OuraCsvRow> Read(TextReader reader)
    {
        var header = ReadRecord(reader);
        if (header is null) yield break;

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
            columns.TryAdd(header[i].Trim().TrimStart('﻿'), i);

        while (ReadRecord(reader) is { } fields)
        {
            if (fields.Count == 1 && header.Count > 1)
            {
                if (fields[0].Length == 0) continue;
                fields = ReadRecord(new StringReader(fields[0])) ?? fields;
            }

            if (fields.Count < header.Count)
            {
                var padding = Enumerable.Repeat(string.Empty, header.Count - fields.Count);
                fields = string.Equals(header[0].Trim(), "id", StringComparison.OrdinalIgnoreCase) &&
                         !Guid.TryParse(fields[0], out _)
                    ? [.. padding, .. fields]
                    : [.. fields, .. padding];
            }

            yield return new OuraCsvRow(columns, fields);
        }
    }

    private static List<string>? ReadRecord(TextReader reader)
    {
        if (reader.Peek() < 0) return null;

        var fields = new List<string>();
        var field = new StringBuilder();
        var total = 0;
        var quoted = false;
        var atFieldStart = true;

        while (true)
        {
            var next = reader.Read();
            if (next < 0) break;
            if (++total > MaxRecordChars) throw new OuraExportException(OuraExportException.TooMuchData);
            var c = (char)next;

            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else quoted = false;
                continue;
            }

            if (c == '"' && atFieldStart)
            {
                quoted = true;
                atFieldStart = false;
                continue;
            }

            if (c == Separator)
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                continue;
            }

            if (c == '\n') break;
            if (c == '\r') continue;

            field.Append(c);
            atFieldStart = false;
        }

        fields.Add(field.ToString());
        return fields;
    }
}

internal sealed class OuraCsvRow(IReadOnlyDictionary<string, int> columns, IReadOnlyList<string> fields)
{
    /// <summary>The trimmed value of <paramref name="column"/>, or null when it is absent or empty.</summary>
    public string? this[string column]
    {
        get
        {
            if (!columns.TryGetValue(column, out var index) || index >= fields.Count) return null;
            var value = fields[index].Trim();
            return value.Length == 0 || value is "null" or "None" ? null : value;
        }
    }
}
