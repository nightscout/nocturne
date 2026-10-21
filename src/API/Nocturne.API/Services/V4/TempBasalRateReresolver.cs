using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Re-resolves the schedule-derived rates of percent temp basals (resolved from the schedule, or
/// still unknown) in the span a schedule, profile or activation change governs. Uploaded rates are
/// never touched.
/// </summary>
public interface ITempBasalRateReresolver
{
    /// <param name="versions">
    /// The basal schedule versions written, moved or deleted, by profile name and start (a moved
    /// version once at each start).
    /// </param>
    Task AfterSchedulesWrittenAsync(
        IReadOnlyCollection<(string ProfileName, DateTime Start)> versions, CancellationToken ct);

    /// <param name="spans">The activations written or removed, with the end they ran to, if any.</param>
    Task AfterActivationsChangedAsync(
        IReadOnlyCollection<(string ProfileName, DateTime Start, DateTime? End)> spans, CancellationToken ct);

    /// <summary>
    /// For a change whose span is not worth working out (the default store, a store's timezone, the
    /// patient's timezone, a source's activations removed): every schedule-derived rate, at any time.
    /// </summary>
    /// <param name="profileNames">The profiles whose cached versions the change made stale.</param>
    Task AfterProfilesChangedAsync(IReadOnlyCollection<string> profileNames, CancellationToken ct);
}

/// <summary>
/// The rate Nightscout gives a percent temp is read against the schedule in force when it is read,
/// so a schedule-derived rate here follows every later change to that schedule: a version, a
/// profile document, the default store, a timezone or an activation, including one an import lands
/// after the temp.
/// </summary>
/// <remarks>
/// Runs after the write has committed and never fails it: an error is logged and the rates are left
/// as they were. It is a system-derived recompute in a job scope of its own that reads as no one, so
/// a history-clamped writer still moves older rates, and no resolver memo of the writing scope
/// answers for the change; the version cache is reloaded for the profiles named. A tenant with no
/// schedule-derived percent temps costs one indexed probe.
/// <para>
/// A version's window runs to the next version of its profile; the newest also covers the time
/// before the first, which reads the newest (<see cref="ProfileVersionCache.ActiveAt{TRecord}"/>),
/// and with no version left every time is covered. A document's stores each get a window from its
/// start, which covers the per-time default store moving there. An activation with no end is
/// open-ended: a permanent one applies again after a temporary one ends
/// (<see cref="ProfileActivations.ProfileActivationSelection"/>).
/// </para>
/// <para>
/// Each row is written by a conditional update (<see cref="ITempBasalRepository.SetScheduleDerivedRateAsync"/>)
/// so a concurrent edit, upload, delete or overlapping re-resolution of it stands.
/// </para>
/// </remarks>
internal sealed class TempBasalRateReresolver(
    IServiceProvider scope,
    ILogger<TempBasalRateReresolver> logger) : ITempBasalRateReresolver
{
    public const string AuditEndpoint = "temp-basal-rates";

    private const int PageSize = 500;

    private sealed record Window(DateTime? From, DateTime? To);

    public Task AfterSchedulesWrittenAsync(
        IReadOnlyCollection<(string ProfileName, DateTime Start)> versions, CancellationToken ct) =>
        versions.Count == 0
            ? Task.CompletedTask
            : RunAsync(versions.Select(v => v.ProfileName), (sp, token) => VersionWindowsAsync(sp, versions, token), ct);

    public Task AfterActivationsChangedAsync(
        IReadOnlyCollection<(string ProfileName, DateTime Start, DateTime? End)> spans, CancellationToken ct) =>
        spans.Count == 0
            ? Task.CompletedTask
            : RunAsync(
                spans.Select(s => s.ProfileName),
                (_, _) => Task.FromResult(spans.Select(s => new Window(s.Start, s.End)).ToList()),
                ct);

    public Task AfterProfilesChangedAsync(IReadOnlyCollection<string> profileNames, CancellationToken ct) =>
        RunAsync(profileNames, (_, _) => Task.FromResult(new List<Window> { new(null, null) }), ct);

    private async Task RunAsync(
        IEnumerable<string> profileNames,
        Func<IServiceProvider, CancellationToken, Task<List<Window>>> windowsOf,
        CancellationToken ct)
    {
        if (scope.GetRequiredService<ITenantAccessor>() is not { IsResolved: true, Context: { } tenant })
            return;

        try
        {
            await using var job = TenantJobScope.Open(scope, tenant, AuditEndpoint);
            var services = job.ServiceProvider;

            var tempBasals = services.GetRequiredService<ITempBasalRepository>();
            if (!await tempBasals.HasScheduleDerivedAsync(ct))
                return;

            var versions = services.GetRequiredService<ProfileVersionCache>();
            var basalSchedules = services.GetRequiredService<IBasalScheduleRepository>();
            var therapySettings = services.GetRequiredService<ITherapySettingsRepository>();
            foreach (var name in profileNames.Distinct(StringComparer.Ordinal))
            {
                await versions.RefreshAsync(basalSchedules, name, ct);
                await versions.RefreshAsync(therapySettings, name, ct);
            }

            var rates = services.GetRequiredService<RelativeTempBasalRates>();
            var changed = 0;
            foreach (var window in Merge(await windowsOf(services, ct)))
                changed += await ReresolveAsync(window, rates, tempBasals, ct);

            if (changed > 0)
            {
                var cache = services.GetRequiredService<ITenantCache>();
                await cache.RemoveFamilyAsync(TenantCacheFamily.Treatments, ct);
                await cache.RemoveFamilyAsync(TenantCacheFamily.Statistics, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not re-resolve the percent temp basal rates a schedule change governs");
        }
    }

    private static async Task<int> ReresolveAsync(
        Window window, RelativeTempBasalRates rates, ITempBasalRepository tempBasals, CancellationToken ct)
    {
        var changed = 0;
        (DateTime, Guid)? after = null;
        while (true)
        {
            var page = await tempBasals.GetScheduleDerivedAsync(window.From, window.To, after, PageSize, ct);
            foreach (var tempBasal in page)
            {
                var percent = tempBasal.Percent!.Value;
                var (rate, scheduled) = await rates.RateAtAsync(percent, tempBasal.StartMills, ct);
                if (rate == tempBasal.Rate && scheduled == tempBasal.ScheduledRate)
                    continue;

                if (await tempBasals.SetScheduleDerivedRateAsync(tempBasal.Id, percent, tempBasal.Rate, rate, scheduled, ct))
                    changed++;
            }

            if (page.Count < PageSize)
                return changed;
            after = (page[^1].StartTimestamp, page[^1].Id);
        }
    }

    private static async Task<List<Window>> VersionWindowsAsync(
        IServiceProvider services,
        IReadOnlyCollection<(string ProfileName, DateTime Start)> written,
        CancellationToken ct)
    {
        var basalSchedules = services.GetRequiredService<IBasalScheduleRepository>();
        var windows = new List<Window>();
        foreach (var group in written.GroupBy(v => v.ProfileName, StringComparer.Ordinal))
        {
            var starts = (await basalSchedules.GetByProfileNameAsync(group.Key, ct))
                .Select(v => v.Timestamp)
                .Order()
                .ToList();
            foreach (var (_, start) in group)
            {
                var next = starts.Where(s => s > start).Select(s => (DateTime?)s).FirstOrDefault();
                if (next is null)
                {
                    windows.Add(starts.Count > 0 ? new Window(null, starts[0]) : new Window(null, null));
                }

                windows.Add(new Window(start, next));
            }
        }

        return windows;
    }

    /// <summary>The windows sorted and joined where they overlap, a null bound being open.</summary>
    private static IEnumerable<Window> Merge(IEnumerable<Window> windows)
    {
        Window? current = null;
        foreach (var window in windows.OrderBy(w => w.From ?? DateTime.MinValue))
        {
            if (current is null)
            {
                current = window;
                continue;
            }

            if (current.To is { } to && (window.From ?? DateTime.MinValue) > to)
            {
                yield return current;
                current = window;
                continue;
            }

            current = current with
            {
                To = current.To is null || window.To is null ? null : Max(current.To.Value, window.To.Value),
            };
        }

        if (current is not null)
            yield return current;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
