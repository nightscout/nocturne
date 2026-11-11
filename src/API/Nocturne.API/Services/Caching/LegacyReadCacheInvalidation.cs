using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Caching;

/// <summary>
/// Clears the legacy read families a written model type projects into: <see cref="TenantCacheFamily.Entries"/>
/// for the glucose tables, <see cref="TenantCacheFamily.Treatments"/> for every table of
/// <see cref="LegacyTreatmentTables.All"/> and for the <see cref="TreatmentFood"/> breakdown a carb
/// intake is served with (<see cref="CarbFoodIndex"/>) and for the <see cref="Food"/> whose name that
/// breakdown reads. Every state span category clears treatments, though only the categories of
/// <see cref="LegacyTreatmentTables.StateSpanTables"/> are served as one.
/// </summary>
internal sealed class LegacyReadCacheInvalidation(
    ITenantCache cache, ILogger<LegacyReadCacheInvalidation> logger) : ILegacyReadCacheInvalidation
{
    internal static readonly IReadOnlyDictionary<Type, string[]> FamiliesByModel =
        LegacyTreatmentTables.All.Select(table => table.Model)
            .Append(typeof(TreatmentFood))
            .Append(typeof(Food))
            .Select(model => (Model: model, Family: TenantCacheFamily.Treatments))
            .Concat(new[] { typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration) }
                .Select(model => (Model: model, Family: TenantCacheFamily.Entries)))
            .GroupBy(pair => pair.Model)
            .ToDictionary(g => g.Key, g => g.Select(pair => pair.Family).Distinct().ToArray());

    public async Task WrittenAsync(IReadOnlyCollection<Type> models)
    {
        var families = models
            .SelectMany(model => FamiliesByModel.GetValueOrDefault(model) ?? [])
            .Distinct();
        foreach (var family in families)
        {
            try
            {
                await cache.RemoveFamilyAsync(family, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clear the {Family} cache after a write", family);
            }
        }
    }
}
