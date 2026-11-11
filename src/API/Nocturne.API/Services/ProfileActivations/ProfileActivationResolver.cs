using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>
/// Resolves activations by <see cref="ProfileActivationSelection"/>, and each one's schedule source.
/// Reads go through the <see cref="TenantCacheFamily.ProfileActivations"/> family, which every
/// activation write clears.
/// </summary>
/// <remarks>
/// NIGHTSCOUT-COMPAT: Nightscout ignores a profile switch older than the profile store document in
/// force (<c>t.mills >= pdataActive.mills</c>, lib/profilefunctions.js), so uploading a new store
/// silently ends a permanent switch. Here a permanent activation runs until another activation
/// supersedes it, whatever the store does after it started.
/// </remarks>
internal sealed class ProfileActivationResolver : IProfileActivationResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private const long MillisPerMinute = 60_000;
    private const int MaxActivationsPerRead = 5_000;

    private readonly IProfileActivationRepository _activations;
    private readonly ProfileStoreTimelineSource _stores;
    private readonly ITenantCache _cache;

    public ProfileActivationResolver(
        IProfileActivationRepository activations,
        ProfileStoreTimelineSource stores,
        ITenantCache cache)
    {
        _activations = activations;
        _stores = stores;
        _cache = cache;
    }

    public async Task<ResolvedProfileActivation?> GetAtAsync(long timeMills, CancellationToken ct = default)
    {
        var eligible = await EligibleAsync(timeMills, timeMills, ct);
        return ProfileActivationSelection.At(eligible, timeMills) is { } active
            ? await ResolveAsync(active, timeMills, ct)
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A read that returns <see cref="MaxActivationsPerRead"/> rows may have dropped the oldest, so its
    /// times are split in two and each half read on its own, until each read is whole or spans one minute.
    /// </remarks>
    public async Task<IReadOnlyList<ResolvedProfileActivation?>> GetAtAsync(
        IReadOnlyList<long> timesMills, CancellationToken ct = default)
    {
        var resolved = new ResolvedProfileActivation?[timesMills.Count];
        var order = Enumerable.Range(0, timesMills.Count).OrderBy(i => timesMills[i]).ToArray();
        if (order.Length > 0)
            await ResolveSliceAsync(order);
        return resolved;

        async Task ResolveSliceAsync(ArraySegment<int> slice)
        {
            var (from, to) = (timesMills[slice[0]], timesMills[slice[^1]]);
            var (eligible, whole) = await ReadEligibleAsync(from, to, ct);
            if (!whole && MinuteOf(from) != MinuteOf(to))
            {
                var half = slice.Count / 2;
                await ResolveSliceAsync(slice[..half]);
                await ResolveSliceAsync(slice[half..]);
                return;
            }

            foreach (var i in slice)
            {
                resolved[i] = ProfileActivationSelection.At(eligible, timesMills[i]) is { } active
                    ? await ResolveAsync(active, timesMills[i], ct)
                    : null;
            }
        }
    }

    public async Task<IReadOnlyList<ResolvedProfileSegment>> GetRangeAsync(
        long fromMills, long toMills, CancellationToken ct = default)
    {
        if (toMills <= fromMills)
            return [];

        var eligible = await EligibleAsync(fromMills, toMills, ct);
        var segments = new List<ResolvedProfileSegment>();
        foreach (var (start, end, active) in ProfileActivationSelection.Segments(eligible, fromMills, toMills))
            segments.Add(new ResolvedProfileSegment(start, end, await ResolveAsync(active, start, ct)));
        return segments;
    }

    private async Task<List<ProfileActivation>> EligibleAsync(long fromMills, long toMills, CancellationToken ct) =>
        (await ReadEligibleAsync(fromMills, toMills, ct)).Eligible;

    /// <remarks>
    /// The read covers whole minutes around [<paramref name="fromMills"/>, <paramref name="toMills"/>], so
    /// lookups and ranges that differ only within a minute share one cached read.
    /// </remarks>
    /// <returns>The eligible activations, and whether the read held every row, short of its cap.</returns>
    private async Task<(List<ProfileActivation> Eligible, bool Whole)> ReadEligibleAsync(
        long fromMills, long toMills, CancellationToken ct)
    {
        var readFrom = MinuteOf(fromMills);
        var readTo = MinuteOf(toMills) + MillisPerMinute;
        var rows = await _cache.GetOrSetAsync(
            $"{TenantCacheFamily.ProfileActivations}:{readFrom}:{readTo}",
            async () => (await _activations.GetOverlappingAsync(
                ToUtc(readFrom), ToUtc(readTo), MaxActivationsPerRead, ct)).ToArray(),
            Ttl, ct);
        var effectiveSources = await _cache.GetOrSetAsync(
            $"{TenantCacheFamily.ProfileActivations}:effective-sources",
            async () => (await _activations.GetEffectiveSourcesAsync(ct))
                .GroupBy(s => ProfileActivationSource.KeyOf(s.DataSource, s.App, s.EnteredBy), StringComparer.Ordinal)
                .Select(g => new FirstEffective(g.Key, g.Min(s => s.FirstStart.ToMills())))
                .ToArray(),
            Ttl, ct);

        return (ProfileActivationSelection.Eligible(
            rows, effectiveSources.ToDictionary(s => s.Source, s => s.Mills, StringComparer.Ordinal)),
            rows.Length < MaxActivationsPerRead);
    }

    /// <summary>When the activations of <paramref name="Source"/> first reported what its pump runs.</summary>
    internal sealed record FirstEffective(string Source, long Mills);

    private async Task<ResolvedProfileActivation> ResolveAsync(
        ProfileActivation activation, long atMills, CancellationToken ct)
    {
        var name = activation.BaseProfileName;
        var snapshot = activation.Snapshot is { HasSchedules: true } schedules ? schedules : null;
        var scaling = CircadianAdjustment.Of(activation);

        if (snapshot is { HasEverySchedule: true } && activation.SnapshotIsCustomized)
            return new ResolvedProfileActivation(activation, name, ProfileScheduleSource.CustomizedSnapshot, snapshot, null);

        if (snapshot is { HasEverySchedule: true } && ProfileActivationSource.IsNative(activation))
            return new ResolvedProfileActivation(activation, name, ProfileScheduleSource.BaseSnapshot, snapshot, scaling);

        // A snapshot missing a kind of schedule gives way to the stored profile, which has every kind.
        if (snapshot is not null && !(await _stores.GetAsync(ct)).Holds(name, atMills))
            return activation.SnapshotIsCustomized
                ? new ResolvedProfileActivation(activation, name, ProfileScheduleSource.CustomizedSnapshot, snapshot, null)
                : new ResolvedProfileActivation(activation, name, ProfileScheduleSource.BaseSnapshot, snapshot, scaling);

        return new ResolvedProfileActivation(activation, name, ProfileScheduleSource.ProfileStore, null, scaling);
    }

    private static long MinuteOf(long mills) =>
        Math.DivRem(mills, MillisPerMinute, out var rem) * MillisPerMinute - (rem < 0 ? MillisPerMinute : 0);

    private static DateTime ToUtc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
}
