using System.Text;
using Nocturne.Core.Models.Translations;

namespace Nocturne.API.Services;

public record PoEditResult
{
    public required string Text { get; init; }
    public int Applied { get; init; }
    public IReadOnlyList<TranslationUnmatchedEntry> Unmatched { get; init; } = [];
}

/// <summary>
/// Applies msgstr updates to a gettext .po catalog while preserving all other
/// content byte-for-byte (comments, references, ordering, unrelated entries).
/// The catalogs are owned by wuchale's extractor; this editor only ever
/// rewrites msgstr lines of existing entries so a contribution can never
/// add, remove, or reorder messages.
/// </summary>
public static class PoCatalogEditor
{
    /// <summary>
    /// entries maps (msgctxt ?? "", msgid) to the new msgstr values. Singular
    /// entries take one value; plural entries must supply exactly as many
    /// values as the catalog entry has msgstr[n] slots, otherwise the entry
    /// is reported unmatched.
    /// </summary>
    public static PoEditResult ApplyTranslations(
        string poText,
        IReadOnlyDictionary<(string Context, string MsgId), IReadOnlyList<string>> entries)
    {
        var newline = poText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = poText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var applied = 0;
        var matchedKeys = new HashSet<(string, string)>();
        var output = new List<string>(lines.Count);

        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Length == 0)
            {
                output.Add(lines[i]);
                i++;
                continue;
            }

            // A block is a run of non-empty lines: one entry incl. comments.
            var start = i;
            while (i < lines.Count && lines[i].Length > 0)
                i++;

            var block = lines[start..i];
            if (TryRewriteBlock(block, entries, matchedKeys, out var rewritten))
            {
                output.AddRange(rewritten);
                applied++;
            }
            else
            {
                output.AddRange(block);
            }
        }

        var unmatched = entries.Keys
            .Where(k => !matchedKeys.Contains(k))
            .Select(k => new TranslationUnmatchedEntry { MsgId = k.MsgId, Context = k.Context })
            .ToList();

        return new PoEditResult
        {
            Text = string.Join(newline, output),
            Applied = applied,
            Unmatched = unmatched,
        };
    }

    private static bool TryRewriteBlock(
        List<string> block,
        IReadOnlyDictionary<(string Context, string MsgId), IReadOnlyList<string>> entries,
        HashSet<(string, string)> matchedKeys,
        out List<string> rewritten)
    {
        rewritten = [];

        var i = 0;
        while (i < block.Count && block[i].StartsWith('#'))
        {
            if (block[i].StartsWith("#~"))
                return false;
            i++;
        }

        var context = "";
        if (i < block.Count && block[i].StartsWith("msgctxt "))
            (context, i) = ReadString(block, i, "msgctxt ");

        if (i >= block.Count || !block[i].StartsWith("msgid "))
            return false;
        string msgId;
        (msgId, i) = ReadString(block, i, "msgid ");

        // msgid "" is the header entry.
        if (msgId.Length == 0 || !entries.TryGetValue((context, msgId), out var values))
            return false;

        var isPlural = i < block.Count && block[i].StartsWith("msgid_plural ");
        if (isPlural)
            (_, i) = ReadString(block, i, "msgid_plural ");

        var msgStrStart = i;
        var msgStrCount = 0;
        // A msgstr's own continuation lines are consumed here; unlike msgid,
        // nothing has read past the keyword line yet.
        while (i < block.Count && (block[i].StartsWith("msgstr") || block[i].StartsWith('"')))
        {
            if (block[i].StartsWith("msgstr"))
                msgStrCount++;
            i++;
        }
        var msgStrEnd = i;

        if (msgStrCount == 0)
            return false;

        List<string> renderedMsgStrs;
        if (isPlural)
        {
            if (values.Count != msgStrCount)
                return false;
            renderedMsgStrs = values
                .Select((v, n) => $"msgstr[{n}] \"{Escape(v)}\"")
                .ToList();
        }
        else
        {
            if (values.Count != 1)
                return false;
            renderedMsgStrs = [$"msgstr \"{Escape(values[0])}\""];
        }

        for (var j = 0; j < block.Count; j++)
        {
            if (j >= msgStrStart && j < msgStrEnd)
            {
                if (j == msgStrStart)
                    rewritten.AddRange(renderedMsgStrs);
                continue;
            }

            // A supplied translation supersedes a fuzzy (machine/stale) one.
            if (block[j].StartsWith("#,") && block[j].Contains("fuzzy"))
            {
                var stripped = StripFuzzyFlag(block[j]);
                if (stripped is not null)
                    rewritten.Add(stripped);
                continue;
            }

            rewritten.Add(block[j]);
        }

        matchedKeys.Add((context, msgId));
        return true;
    }

    private static (string Value, int Next) ReadString(List<string> lines, int i, string keyword)
    {
        var sb = new StringBuilder();
        sb.Append(Unescape(ExtractQuoted(lines[i][keyword.Length..])));
        i++;
        while (i < lines.Count && lines[i].StartsWith('"'))
        {
            sb.Append(Unescape(ExtractQuoted(lines[i])));
            i++;
        }
        return (sb.ToString(), i);
    }

    private static string? StripFuzzyFlag(string flagsLine)
    {
        var flags = flagsLine[2..]
            .Split(',')
            .Select(f => f.Trim())
            .Where(f => f.Length > 0 && f != "fuzzy")
            .ToList();
        return flags.Count == 0 ? null : "#, " + string.Join(", ", flags);
    }

    private static string ExtractQuoted(string line)
    {
        var trimmed = line.Trim();
        var first = trimmed.IndexOf('"');
        var last = trimmed.LastIndexOf('"');
        return first >= 0 && last > first ? trimmed[(first + 1)..last] : "";
    }

    internal static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }

            i++;
            sb.Append(s[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => s[i],
            });
        }
        return sb.ToString();
    }

    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
