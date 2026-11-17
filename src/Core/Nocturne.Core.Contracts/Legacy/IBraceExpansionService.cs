using Nocturne.Core.Models.Queries;

namespace Nocturne.Core.Contracts.Legacy;

/// <summary>
/// Service for handling bash-style brace expansion for time pattern matching
/// Provides 1:1 compatibility with the legacy JavaScript braces.expand() functionality
/// </summary>
public interface IBraceExpansionService
{
    /// <summary>
    /// Expand a bash-style brace pattern into all matching strings.
    /// </summary>
    /// <remarks>
    /// Supports numeric ranges (e.g., <c>{01..12}</c>), comma lists (e.g., <c>{a,b,c}</c>),
    /// and nested patterns.
    /// </remarks>
    /// <param name="pattern">Brace pattern to expand (e.g., <c>"20{14..16}-{01..12}"</c>).</param>
    /// <returns>All expanded string permutations.</returns>
    /// <exception cref="TimePatternRejectedException">
    /// The pattern exceeds a <see cref="TimePatternLimits"/> bound.
    /// </exception>
    IEnumerable<string> ExpandBraces(string pattern);

    /// <summary>
    /// Prepare a <see cref="TimePatternQuery"/> from prefix and regex parameters for database querying.
    /// </summary>
    /// <param name="prefix">Time prefix pattern (e.g., <c>"2015-04"</c>).</param>
    /// <param name="regex">Time regex pattern (e.g., <c>"T{13..18}:{00..15}"</c>).</param>
    /// <param name="fieldName">Database field name to match against (default <c>"dateString"</c>).</param>
    /// <returns>A <see cref="TimePatternQuery"/> ready for database execution.</returns>
    /// <exception cref="TimePatternRejectedException">Either expansion exceeds a <see cref="TimePatternLimits"/> bound.</exception>
    TimePatternQuery PrepareTimePatterns(
        string? prefix,
        string? regex,
        string fieldName = "dateString"
    );
}

/// <summary>
/// Result of time pattern preparation for queries
/// </summary>
public class TimePatternQuery
{
    public IEnumerable<string> Patterns { get; set; } = Array.Empty<string>();
    public string FieldName { get; set; } = string.Empty;
    public IEnumerable<string> InPatterns { get; set; } = Array.Empty<string>();
    public string? SingleRegexPattern { get; set; }
    public bool CanOptimizeWithIndex { get; set; }
}

/// <summary>
/// Bounds on brace expansion. Upstream's <c>braces</c> refuses a range past 1000 values; the other
/// caps keep nested groups from multiplying past it. Expansion visits at most
/// <see cref="MaxExpansions"/> times <see cref="MaxGroups"/> patterns, and the expansions are joined
/// into one <c>$regex</c>, so together they must fit <see cref="BoundedRegex.MaxLength"/>.
/// </summary>
public static class TimePatternLimits
{
    public const int MaxPatternLength = 512;
    public const int MaxGroups = 16;
    public const int MaxRangeSize = 1000;
    public const int MaxExpansions = 1000;
    public const int MaxExpandedLength = BoundedRegex.MaxLength;
}

/// <summary>A time pattern the API will not evaluate. Its message is safe to return to the caller.</summary>
public sealed class TimePatternRejectedException(string message) : Exception(message);
