using System.Globalization;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Treatments;

/// <summary>Insulin and carbs on board now, and where each figure came from.</summary>
/// <param name="Iob">Insulin on board in units.</param>
/// <param name="IobDisplay">The IOB as the iob plugin displays it.</param>
/// <param name="IobSource">The system that reported the IOB, or <c>null</c> when nothing did.</param>
/// <param name="Cob">Carbs on board in grams.</param>
/// <param name="CobSource">The system that reported the COB, or <c>null</c> when nothing did.</param>
public sealed record CurrentIobCob(
    double Iob,
    string IobDisplay,
    string? IobSource,
    double Cob,
    string? CobSource
);

/// <summary>
/// Reads insulin and carbs on board at the present moment, from the AID system when it reported
/// recently and from treatments otherwise.
/// </summary>
/// <seealso cref="CurrentIobCobReader"/>
public interface ICurrentIobCobReader
{
    Task<CurrentIobCob> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="ICurrentIobCobReader"/>
/// <remarks>
/// The choice between a device figure and a calculated one is
/// <see cref="IIobCalculator.CalculateTotalAsync"/> and <see cref="ICobCalculator.CalculateTotalAsync"/>:
/// they take the newest AID snapshot within
/// <see cref="Nocturne.Core.Clinical.Treatments.DeviceReportedValues.RecencyThresholdMs"/> before
/// they use treatments. The result is held in the local tier of <see cref="ITenantCache"/> for a
/// minute, so a history-clamped reader, which has no cache partition, always computes it.
/// </remarks>
public sealed class CurrentIobCobReader(
    IIobCalculator iobCalculator,
    ICobCalculator cobCalculator,
    IBolusRepository bolusRepository,
    ITempBasalRepository tempBasalRepository,
    ICarbIntakeRepository carbIntakeRepository,
    ITenantCache cache
) : ICurrentIobCobReader
{
    private const string CacheKey = "iobcob:current";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public Task<CurrentIobCob> GetAsync(CancellationToken ct = default) =>
        cache.GetOrCreateLocalAsync(CacheKey, () => ComputeAsync(ct), CacheDuration);

    private async Task<CurrentIobCob> ComputeAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (boluses, tempBasals) = await RecentTreatments.LoadInsulinAsync(
            bolusRepository, tempBasalRepository, ct);
        var carbIntakes = await RecentTreatments.LoadCarbsAsync(carbIntakeRepository, ct);

        var iob = await iobCalculator.CalculateTotalAsync(boluses, tempBasals, now, ct);
        var cob = await cobCalculator.CalculateTotalAsync(carbIntakes, boluses, tempBasals, now, ct);

        var iobValue = Math.Round(iob.Iob, 2);
        return new CurrentIobCob(
            iobValue,
            iobValue == 0 ? "0" : iobValue.ToString("F2", CultureInfo.InvariantCulture),
            iob.Source,
            Math.Round(cob.Cob, 1),
            cob.Source
        );
    }
}
