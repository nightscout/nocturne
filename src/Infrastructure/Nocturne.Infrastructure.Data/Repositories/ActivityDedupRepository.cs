using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IActivityDedupRepository"/> over <see cref="NocturneDbContext"/>, one context of the
/// scope's reader per call.
/// </summary>
public class ActivityDedupRepository : IActivityDedupRepository
{
    private readonly INocturneDbContextFactory _contexts;

    public ActivityDedupRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetHeldIdsAsync(
        IReadOnlyCollection<string> activityIds, CancellationToken ct = default)
    {
        var ids = activityIds.ToHashSet(StringComparer.Ordinal);
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Count == 0)
            return held;

        await using var db = await _contexts.CreateAsync(ct);
        var lookups = new Func<Task<IReadOnlySet<string>>>[]
        {
            () => db.GetHeldOriginalIdsAsync<StateSpanEntity>(ids, ct),
            () => db.GetHeldOriginalIdsAsync<HeartRateEntity>(ids, ct),
            () => db.GetHeldOriginalIdsAsync<StepCountEntity>(ids, ct),
            () => db.GetHeldOriginalIdsAsync<SleepSessionEntity>(
                ids, s => s.Source == ActivityStateSpanMapper.StoredSleepSessionSource, ct),
        };

        foreach (var lookup in lookups)
        {
            if (ids.Count == held.Count)
                break;

            held.UnionWith(await lookup());
        }

        return held;
    }
}
