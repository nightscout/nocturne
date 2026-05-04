using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the effective basal rate at a given time by combining the scheduled basal
/// from <see cref="IBasalRateResolver"/> with any active <see cref="TempBasal"/> override.
/// </summary>
internal sealed class TempBasalResolver : ITempBasalResolver
{
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly ITempBasalRepository _tempBasalRepo;
    private readonly ILogger<TempBasalResolver> _logger;

    public TempBasalResolver(
        IBasalRateResolver basalRateResolver,
        ITempBasalRepository tempBasalRepo,
        ILogger<TempBasalResolver> logger)
    {
        _basalRateResolver = basalRateResolver;
        _tempBasalRepo = tempBasalRepo;
        _logger = logger;
    }

    public async Task<TempBasalResolverResult> GetTempBasalAsync(
        long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var scheduledBasal = await _basalRateResolver.GetBasalRateAsync(timeMills, specProfile, ct);

        var queryTime = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        // Query temp basals that overlap the requested time.
        // We look for records where StartTimestamp <= queryTime, ordered newest-first, limit 1.
        var tempBasals = await _tempBasalRepo.GetAsync(
            from: null,
            to: queryTime,
            device: null,
            source: null,
            limit: 10,
            offset: 0,
            descending: true,
            ct: ct);

        var activeTempBasal = tempBasals
            .Where(t => t.StartTimestamp <= queryTime && (!t.EndTimestamp.HasValue || t.EndTimestamp.Value > queryTime))
            .OrderByDescending(t => t.StartTimestamp)
            .FirstOrDefault();

        if (activeTempBasal is null)
            return new TempBasalResolverResult(scheduledBasal, null, null, scheduledBasal);

        var tempRate = activeTempBasal.Rate;
        return new TempBasalResolverResult(scheduledBasal, tempRate, null, tempRate);
    }
}
