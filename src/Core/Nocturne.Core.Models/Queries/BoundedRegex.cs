using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Nocturne.Core.Models.Queries;

/// <summary>
/// A caller-supplied regular expression, compiled once for the linear-time engine so a match costs
/// time proportional to its input whatever the pattern's shape. Make one per request and reuse it
/// across rows.
/// </summary>
/// <remarks>
/// A pattern that does not compile matches nothing: invalid syntax, longer than
/// <see cref="MaxLength"/>, a backreference or lookaround, or an automaton past the engine's size
/// limit. Matching still slows when the engine falls back from its DFA, so the instance carries one
/// budget across all its matches; a match that overruns <see cref="MatchTimeout"/>, or one that
/// exhausts the budget, abandons the pattern and every later match is false.
/// </remarks>
public sealed class BoundedRegex
{
    /// <summary>
    /// The longest pattern compiled. A v1 times pattern joins up to 1000 brace expansions into one
    /// alternation, so this sits above the roughly ten thousand characters such a pattern can reach.
    /// </summary>
    public const int MaxLength = 16_384;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(1);

    private readonly Regex? _regex;
    private readonly long _budgetTicks;
    private long _spentTicks;
    private volatile bool _abandoned;

    private BoundedRegex(Regex? regex, TimeSpan budget)
    {
        _regex = regex;
        _budgetTicks = (long)(budget.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary><c>false</c> when the pattern did not compile, so every match is false.</summary>
    public bool IsCompiled => _regex is not null;

    /// <summary>
    /// <c>true</c> once a timeout or the spent budget has stopped matching, so a scan that read
    /// through this instance may have under-matched.
    /// </summary>
    public bool Abandoned => _abandoned;

    public static BoundedRegex Create(string pattern, RegexOptions options = RegexOptions.None) =>
        Create(pattern, options, DefaultBudget);

    internal static BoundedRegex Create(string pattern, RegexOptions options, TimeSpan budget)
    {
        if (pattern.Length > MaxLength)
            return new BoundedRegex(null, budget);

        try
        {
            return new BoundedRegex(
                new Regex(pattern, options | RegexOptions.NonBacktracking, MatchTimeout), budget);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new BoundedRegex(null, budget);
        }
    }

    public bool IsMatch(string input)
    {
        if (_regex is null || _abandoned)
            return false;

        var started = Stopwatch.GetTimestamp();
        try
        {
            return _regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            _abandoned = true;
            return false;
        }
        finally
        {
            if (Interlocked.Add(ref _spentTicks, Stopwatch.GetTimestamp() - started) >= _budgetTicks)
                _abandoned = true;
        }
    }
}
