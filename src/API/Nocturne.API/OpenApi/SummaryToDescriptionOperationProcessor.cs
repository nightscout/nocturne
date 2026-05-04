using System.Text.RegularExpressions;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Moves the XML <c>&lt;summary&gt;</c> text (which NSwag maps to <c>summary</c>) into
/// <c>description</c>, then replaces <c>summary</c> with a human-readable title derived
/// from the action method name (e.g. <c>CreateEntries</c> → "Create entries").
/// </summary>
public sealed partial class SummaryToDescriptionOperationProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        var op = context.OperationDescription.Operation;

        // Move current summary → description (only if description is empty).
        if (!string.IsNullOrWhiteSpace(op.Summary) && string.IsNullOrWhiteSpace(op.Description))
            op.Description = op.Summary;

        // Derive a readable title from the method name.
        var methodName = context.MethodInfo.Name;

        // Strip common suffixes: "Async"
        if (methodName.EndsWith("Async", StringComparison.Ordinal))
            methodName = methodName[..^"Async".Length];

        op.Summary = Humanize(methodName);

        return true;
    }

    internal static string Humanize(string pascalName)
    {
        // "CreateEntries" → ["Create", "Entries"]
        var words = PascalCaseBoundary().Split(pascalName);

        // Rejoin adjacent fragments that form a known compound (e.g. "O"+"Auth" → "OAuth").
        words = CoalesceCompounds(words);

        for (var i = 0; i < words.Length; i++)
        {
            if (PreservedWords.Contains(words[i]))
                continue;

            // First word: uppercase first letter, lowercase the rest.
            // Subsequent words: fully lowercase.
            words[i] = i == 0
                ? char.ToUpper(words[i][0]) + words[i][1..].ToLowerInvariant()
                : words[i].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Words that should never be lowercased. Add entries here when a new acronym
    /// appears in a controller method name and renders incorrectly.
    /// </summary>
    private static readonly HashSet<string> PreservedWords = new(StringComparer.Ordinal)
    {
        "A1C", "API", "BG", "CGM", "DData",
        "GMI", "GRI", "ID", "MGDL", "MMOL",
        "OAuth", "OIDC", "SGV", "UI",
    };

    /// <summary>
    /// Adjacent PascalCase fragments that should be merged back into a single token.
    /// The regex splits "OAuth" into ["O", "Auth"] — this table glues them back.
    /// </summary>
    private static readonly Dictionary<string, string> Compounds = new(StringComparer.Ordinal)
    {
        ["O+Auth"] = "OAuth",
    };

    private static string[] CoalesceCompounds(string[] words)
    {
        if (Compounds.Count == 0)
            return words;

        var result = new List<string>(words.Length);
        for (var i = 0; i < words.Length; i++)
        {
            if (i + 1 < words.Length)
            {
                var pair = $"{words[i]}+{words[i + 1]}";
                if (Compounds.TryGetValue(pair, out var merged))
                {
                    result.Add(merged);
                    i++; // skip next
                    continue;
                }
            }

            result.Add(words[i]);
        }

        return result.ToArray();
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex PascalCaseBoundary();
}
