using System.ComponentModel.DataAnnotations;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Reports;

/// <summary>A suggested flag rule: a fact, its range and a value, which the editor offers first.</summary>
/// <param name="Value">In the fact's unit.</param>
public sealed record FlagRulePreset(
    [property: Required] string Dataset,
    [property: Required] string Field,
    int? Days,
    [property: Required] FlagRuleComparison Comparison,
    [property: Required] double Value);

/// <summary>The facts a flag rule may name, and the checks a rule takes before it is saved.</summary>
public interface IFlagRuleCatalogue
{
    /// <summary>
    /// Every flag fact, as the custom reports catalogue lists them. A fact's scopes are per person, so each field is
    /// listed as permitted here; who a rule cannot be checked for is <see cref="CannotCheckCounts"/>.
    /// </summary>
    IReadOnlyList<CustomReportFlagFact> Facts { get; }

    /// <summary>The suggested rules, each a fact of <see cref="Facts"/>.</summary>
    IReadOnlyList<FlagRulePreset> Presets { get; }

    /// <summary>
    /// What is wrong with <paramref name="draft"/>'s fact, keyed <c>dataset</c>, <c>field</c>, <c>days</c> or
    /// <c>value</c>, by the reports validator's rules; empty when it checks.
    /// </summary>
    IReadOnlyDictionary<string, string[]> Check(FlagRuleDraft draft);

    /// <summary>
    /// For each of <paramref name="rules"/>, how many of <paramref name="people"/> it cannot be checked for: limited to
    /// 24 hours, or missing a scope the fact needs. Nothing of any person is read.
    /// </summary>
    IReadOnlyList<int> CannotCheckCounts(
        IReadOnlyList<FlagRule> rules, IReadOnlyList<GlucoseReadTenant> people, bool credentialLimitTo24Hours);
}

/// <inheritdoc cref="IFlagRuleCatalogue"/>
internal sealed class FlagRuleCatalogue(IReportDatasetRegistry registry, FlagFactEvaluator evaluator) : IFlagRuleCatalogue
{
    private const string Statistics = GlucoseStatisticsParameters.Name;
    private const string Status = CurrentStatusParameters.Name;
    private const int PresetDays = 14;

    /// <summary>
    /// Each consensus range fact sits beside its own range twin, so a viewer sees both families side by side. Values are
    /// in each fact's unit: percent, or hours.
    /// </summary>
    internal static readonly IReadOnlyList<FlagRulePreset> PresetRules =
    [
        new(Statistics, "timeBelow", PresetDays, FlagRuleComparison.Above, 4),
        new(Statistics, "timeBelowOwnRange", PresetDays, FlagRuleComparison.Above, 4),
        new(Statistics, "timeAbove", PresetDays, FlagRuleComparison.Above, 25),
        new(Statistics, "timeAboveOwnRange", PresetDays, FlagRuleComparison.Above, 25),
        new(Statistics, GlucoseStatisticsDataset.CoverageField, PresetDays, FlagRuleComparison.Below, 70),
        new(Status, CurrentStatusDataset.HoursSinceReading, null, FlagRuleComparison.Above, 2),
        new(Status, CurrentStatusDataset.SensorHoursLeft, null, FlagRuleComparison.Below, 24),
        new(Status, CurrentStatusDataset.SiteHoursLeft, null, FlagRuleComparison.Below, 24),
    ];

    private static readonly IReadOnlySet<string> AnyScope = new HashSet<string> { Scope.FullAccess };

    public IReadOnlyList<CustomReportFlagFact> Facts { get; } = ReportCatalogue.FlagFactEntries(registry, AnyScope);

    public IReadOnlyList<FlagRulePreset> Presets => PresetRules;

    public IReadOnlyDictionary<string, string[]> Check(FlagRuleDraft draft)
    {
        var (_, errors) = evaluator.CheckRule(draft.Dataset, draft.Field, draft.Days, draft.Comparison, draft.Value);
        return errors
            .GroupBy(e => KeyOf(e.Path))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct().ToArray());
    }

    public IReadOnlyList<int> CannotCheckCounts(
        IReadOnlyList<FlagRule> rules, IReadOnlyList<GlucoseReadTenant> people, bool credentialLimitTo24Hours) =>
        [.. rules.Select(rule =>
            evaluator.CheckRule(rule.Dataset, rule.Field, rule.Days, rule.Comparison, rule.Value).Condition is { } condition
                ? people.Count(person => FlagFactEvaluator.Uncheckable(person, condition, credentialLimitTo24Hours) is not null)
                : people.Count)];

    /// <summary>The rule's key an error of the condition check names.</summary>
    private static string KeyOf(string path) => path switch
    {
        _ when path.StartsWith("dataset", StringComparison.Ordinal) => "dataset",
        "field" or "days" or "value" => path,
        _ => "field",
    };
}
