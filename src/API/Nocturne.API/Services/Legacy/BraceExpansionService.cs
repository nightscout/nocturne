using System.Globalization;
using System.Text.RegularExpressions;
using Nocturne.Core.Contracts.Legacy;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// Implements bash-style brace expansion for time-pattern matching,
/// replicating the behaviour of the legacy JavaScript <c>braces.expand()</c> library.
/// </summary>
/// <seealso cref="IBraceExpansionService"/>
public class BraceExpansionService : IBraceExpansionService
{
    private static readonly Regex NumericRange = new(@"\{(\d+)\.\.(\d+)\}", RegexOptions.CultureInvariant);
    private static readonly Regex Sequence = new(@"\{([^}]+)\}", RegexOptions.CultureInvariant);

    /// <inheritdoc/>
    /// <remarks>
    /// Supports numeric ranges (e.g. <c>{14..16}</c>, or descending <c>{16..14}</c>) and
    /// comma-separated alternatives (e.g. <c>{foo,bar}</c>). Nested braces and leading-zero padding
    /// are also handled. Every group yields at least one string, which is what bounds the work by
    /// <see cref="TimePatternLimits"/>.
    /// </remarks>
    public IEnumerable<string> ExpandBraces(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return [""];

        if (pattern.Length > TimePatternLimits.MaxPatternLength)
            throw new TimePatternRejectedException(
                $"A time pattern may be at most {TimePatternLimits.MaxPatternLength} characters");

        if (pattern.Count(c => c == '{') > TimePatternLimits.MaxGroups)
            throw new TimePatternRejectedException(
                $"A time pattern may have at most {TimePatternLimits.MaxGroups} brace groups");

        var results = new Expansion();
        Expand(pattern, results);
        return results.Patterns;
    }

    private sealed class Expansion
    {
        public List<string> Patterns { get; } = [];
        public int Length { get; set; }
    }

    private static void Expand(string pattern, Expansion results)
    {
        var range = NumericRange.Match(pattern);
        if (range.Success
            && int.TryParse(range.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && int.TryParse(range.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
        {
            if (Math.Abs((long)end - start) >= TimePatternLimits.MaxRangeSize)
                throw new TimePatternRejectedException(
                    $"A time pattern range may span at most {TimePatternLimits.MaxRangeSize} values");

            var padLength = Math.Max(range.Groups[1].Length, range.Groups[2].Length);
            var step = start <= end ? 1 : -1;
            for (var i = start; ; i += step)
            {
                var padded = i.ToString(CultureInfo.InvariantCulture).PadLeft(padLength, '0');
                Expand(pattern.Replace(range.Value, padded), results);
                if (i == end)
                    return;
            }
        }

        var sequence = Sequence.Match(pattern);
        if (sequence.Success)
        {
            foreach (var part in sequence.Groups[1].Value.Split(','))
                Expand(pattern.Replace(sequence.Value, part.Trim()), results);
            return;
        }

        if (results.Patterns.Count == TimePatternLimits.MaxExpansions)
            throw new TimePatternRejectedException(
                $"A time pattern may expand to at most {TimePatternLimits.MaxExpansions} patterns");

        results.Length += pattern.Length + 1;
        if (results.Length > TimePatternLimits.MaxExpandedLength)
            throw new TimePatternRejectedException(
                $"A time pattern may expand to at most {TimePatternLimits.MaxExpandedLength} characters");

        results.Patterns.Add(pattern);
    }

    /// <summary>
    /// Prepare MongoDB query patterns for time-based searches
    /// Replicates the legacy prep_patterns middleware functionality
    /// </summary>
    public TimePatternQuery PrepareTimePatterns(
        string? prefix,
        string? regex,
        string fieldName = "dateString"
    )
    {
        var pattern = new List<string>();

        // Initialize a basic prefix and perform bash brace/glob-style expansion
        var expandedPrefix = ExpandBraces(prefix ?? ".*").ToList();

        // If expansion leads to more than one prefix
        if (expandedPrefix.Count > 1)
        {
            // Pre-pend the prefix to the pattern list and wait to expand it as
            // part of the full pattern
            pattern.Add($"^{prefix}");
        }

        // Append any regex parameters
        if (!string.IsNullOrEmpty(regex))
        {
            // Prepend "match any" rule to their rule
            pattern.Add($".*{regex}");
        }

        // Create a single pattern with all inputs considered
        // Expand the pattern using bash/glob style brace expansion to generate
        // an array of patterns.
        var combinedPattern = string.Join("", pattern);
        var expandedPatterns = ExpandBraces(combinedPattern).ToList();

        if (expandedPatterns.Count == 0)
        {
            expandedPatterns = new List<string> { "" };
        }

        // Prepare the MongoDB query
        var result = new TimePatternQuery
        {
            Patterns = expandedPatterns,
            FieldName = fieldName,
            InPatterns = expandedPatterns,
        };

        // If there is a single prefix pattern, MongoDB can optimize this against
        // an indexed field
        if (expandedPrefix.Count == 1)
        {
            result.SingleRegexPattern = $"^{expandedPrefix.First()}";
            result.CanOptimizeWithIndex = true;
        }

        return result;
    }
}
