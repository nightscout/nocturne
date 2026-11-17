using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Services.Analytics;

internal sealed partial class LoopAnalysisService
{
    /// <summary>
    /// How far before the range the Settings evidence load reads. A basal block is rejected for a
    /// meal-like climb that peaked within <see cref="ObservedBasalNeed.CarbFreeMs"/> before it; such a
    /// climb starts up to <see cref="UnannouncedRises.MergeMs"/> before its peak, is found only after
    /// a <see cref="UnannouncedRises.StableMs"/> stretch before that, and is told from an announced meal
    /// by carbs up to <see cref="UnannouncedRises.CarbsBeforeMs"/> before its start. That reaches past
    /// <see cref="ObservedBasalNeed.ManualBolusFreeMs"/> and every window's own look back.
    /// </summary>
    private const long SettingsEvidenceBeforeMs =
        ObservedBasalNeed.CarbFreeMs + UnannouncedRises.MergeMs + UnannouncedRises.StableMs + UnannouncedRises.CarbsBeforeMs;

    /// <summary>
    /// How far past the range it reads, for climb detection alone: every window past the range's end
    /// is rejected as <see cref="SettingsEvidenceRule.OutsideRange"/>, but a climb under way at the
    /// end is found only with the <see cref="UnannouncedRises.RiseWindowMs"/> after its start that
    /// shows it rising.
    /// </summary>
    private const long SettingsEvidenceAfterMs = UnannouncedRises.RiseWindowMs;

    public async Task<SettingsEvidenceReport> GetSettingsEvidenceAsync(
        LoopAnalysisFilter filter, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var (primary, comparison, names) = await ForRangesAsync(
            filter,
            range => LoadSettingsEvidenceAsync(range, zone, filter, ct),
            (loaded, range, isPrimary) => SettingsEvidenceCalculator.Calculate(
                loaded.Inputs, Mask(filter, range, zone, loaded.Sources), range.From, range.To, listEpisodes: isPrimary));
        return new SettingsEvidenceReport { Primary = primary, Comparison = comparison, ProfileNames = names };
    }

    public async Task<List<string>> GetProfileNamesAsync(LoopAnalysisFilter filter, CancellationToken ct = default)
    {
        Window[] windows = filter.CompareFrom is { } compareFrom && filter.CompareTo is { } compareTo
            ? [Window.Of(filter.From, filter.To), Window.Of(compareFrom, compareTo)]
            : [Window.Of(filter.From, filter.To)];
        var spans = new List<ProfileSpan>();
        foreach (var window in windows)
            spans.AddRange(await activeProfileResolver.GetProfileSpansForRangeAsync(window.From, window.To, ct));
        return [.. spans.OrderBy(p => p.StartMills).Select(p => p.ProfileName).Distinct(StringComparer.Ordinal)];
    }

    private sealed record SettingsEvidenceLoaded(SettingsEvidenceInputs Inputs) : ILoaded
    {
        public LoopAnalysisMaskSources Sources => Inputs.Sources;
    }

    /// <summary>
    /// The Daily profile's reads over <paramref name="range"/> widened by
    /// <see cref="SettingsEvidenceBeforeMs"/> and <see cref="SettingsEvidenceAfterMs"/>, with the
    /// therapy timeline they were read against.
    /// </summary>
    private async Task<SettingsEvidenceLoaded> LoadSettingsEvidenceAsync(
        Window range, TimeZoneInfo zone, LoopAnalysisFilter filter, CancellationToken ct)
    {
        var padded = new Window(range.From - SettingsEvidenceBeforeMs, range.To + SettingsEvidenceAfterMs);
        var loaded = await LoadDailyProfileAsync(padded, zone, filter, ct);
        return new SettingsEvidenceLoaded(new SettingsEvidenceInputs
        {
            Series = loaded.Inputs,
            Sources = loaded.Sources,
            Therapy = loaded.Timeline,
        });
    }
}
