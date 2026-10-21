using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
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
    private const long MillisPerDay = 86_400_000;
    private const int MaxActivationsPerRead = 5_000;

    private readonly IProfileActivationRepository _activations;
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly ProfileVersionCache _versions;
    private readonly ITenantCache _cache;

    public ProfileActivationResolver(
        IProfileActivationRepository activations,
        ITherapySettingsRepository therapySettings,
        ProfileVersionCache versions,
        ITenantCache cache)
    {
        _activations = activations;
        _therapySettings = therapySettings;
        _versions = versions;
        _cache = cache;
    }

    public async Task<ResolvedProfileActivation?> GetAtAsync(long timeMills, CancellationToken ct = default)
    {
        var eligible = await EligibleAsync(timeMills, timeMills, ct);
        return ProfileActivationSelection.At(eligible, timeMills) is { } active
            ? await ResolveAsync(active, timeMills, ct)
            : null;
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

    /// <remarks>
    /// The read covers whole minutes around [<paramref name="fromMills"/>, <paramref name="toMills"/>], so
    /// lookups and ranges that differ only within a minute share one cached read.
    /// </remarks>
    private async Task<List<ProfileActivation>> EligibleAsync(long fromMills, long toMills, CancellationToken ct)
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
                .Select(g => new FirstEffective(g.Key, g.Min(s => ToMills(s.FirstStart))))
                .ToArray(),
            Ttl, ct);

        return ProfileActivationSelection.Eligible(
            rows, effectiveSources.ToDictionary(s => s.Source, s => s.Mills, StringComparer.Ordinal));
    }

    /// <summary>When the activations of <paramref name="Source"/> first reported what its pump runs.</summary>
    internal sealed record FirstEffective(string Source, long Mills);

    private async Task<ResolvedProfileActivation> ResolveAsync(
        ProfileActivation activation, long atMills, CancellationToken ct)
    {
        var name = activation.BaseProfileName;
        var snapshot = activation.Snapshot is { HasSchedules: true } schedules ? schedules : null;
        var scaling = new CircadianAdjustment(activation.Percentage, activation.TimeshiftMs % MillisPerDay);

        if (snapshot is { HasEverySchedule: true } && activation.SnapshotIsCustomized)
            return new ResolvedProfileActivation(activation, name, ProfileScheduleSource.CustomizedSnapshot, snapshot, null);

        // A snapshot missing a kind of schedule gives way to the stored profile, which has every kind.
        if (snapshot is not null && await _versions.GetActiveAtAsync(_therapySettings, name, ToUtc(atMills), ct) is null)
            return activation.SnapshotIsCustomized
                ? new ResolvedProfileActivation(activation, name, ProfileScheduleSource.CustomizedSnapshot, snapshot, null)
                : new ResolvedProfileActivation(activation, name, ProfileScheduleSource.BaseSnapshot, snapshot, scaling);

        return new ResolvedProfileActivation(activation, name, ProfileScheduleSource.ProfileStore, null, scaling);
    }

    private static long MinuteOf(long mills) =>
        Math.DivRem(mills, MillisPerMinute, out var rem) * MillisPerMinute - (rem < 0 ? MillisPerMinute : 0);

    private static DateTime ToUtc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

    private static long ToMills(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
