using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Re-resolves the schedule-derived rates of percent temp basals (resolved from the schedule, or
/// still unknown) in the span a schedule or profile activation write changes. Uploaded rates are
/// never touched.
/// </summary>
public interface ITempBasalRateReresolver
{
    /// <param name="versions">The basal schedule versions written, by profile name and start.</param>
    Task AfterSchedulesWrittenAsync(
        IReadOnlyCollection<(string ProfileName, DateTime Start)> versions, CancellationToken ct);

    /// <param name="spans">The activations written or removed, with the end they ran to, if any.</param>
    Task AfterActivationsChangedAsync(
        IReadOnlyCollection<(string ProfileName, DateTime Start, DateTime? End)> spans, CancellationToken ct);
}

/// <summary>
/// The rate Nightscout gives a percent temp is read against the schedule in force when it is read,
/// so a schedule-derived rate here follows every later write that changes that schedule: a new
/// version, a profile document, or an activation, including one an import lands after the temp.
/// </summary>
/// <remarks>
/// Runs after the write has committed and never fails it: an error is logged and the rates are
/// left as they were. It reads in a scope of its own, as the writer's reader, so no resolver memo of
/// the writing scope answers for the schedule just written; the version cache is reloaded for it. A window runs
/// from a written version or activation to the next one's start, or to now; a written version that
/// is its profile's newest also covers the time before the first, which reads the newest
/// (<see cref="Profiles.Resolvers.ProfileVersionCache.ActiveAt{TRecord}"/>). A profile document can
/// also move the default store from its start, and each of its stores gets a window from that start,
/// so the default's change is covered too. Each row
/// is written by a conditional update (<see cref="ITempBasalRepository.SetScheduleDerivedRateAsync"/>)
/// so a concurrent edit, upload or delete of it stands.
/// </remarks>
internal sealed class TempBasalRateReresolver(
    IServiceProvider scope,
    ILogger<TempBasalRateReresolver> logger) : ITempBasalRateReresolver
{
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
            : RunAsync(spans.Select(s => s.ProfileName), (sp, token) => ActivationWindowsAsync(sp, spans, token), ct);

    private async Task RunAsync(
        IEnumerable<string> profileNames,
        Func<IServiceProvider, CancellationToken, Task<List<Window>>> windowsOf,
        CancellationToken ct)
    {
        var reader = TenantReader.Of(scope);
        if (reader.Tenant is null)
            return;

        try
        {
            await using var job = TenantJobScope.OpenFor(scope, reader, "temp-basal-rates");
            var services = job.ServiceProvider;

            var versions = services.GetRequiredService<ProfileVersionCache>();
            var basalSchedules = services.GetRequiredService<IBasalScheduleRepository>();
            var therapySettings = services.GetRequiredService<ITherapySettingsRepository>();
            foreach (var name in profileNames.Distinct(StringComparer.Ordinal))
            {
                await versions.RefreshAsync(basalSchedules, name, ct);
                await versions.RefreshAsync(therapySettings, name, ct);
            }

            var rates = services.GetRequiredService<RelativeTempBasalRates>();
            var tempBasals = services.GetRequiredService<ITempBasalRepository>();
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
            logger.LogWarning(ex, "Could not re-resolve the percent temp basal rates a schedule write changed");
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

                if (await tempBasals.SetScheduleDerivedRateAsync(tempBasal.Id, percent, rate, scheduled, ct))
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
                windows.Add(new Window(start, next));
                if (next is null && starts.Count > 0)
                    windows.Add(new Window(null, starts[0]));
            }
        }

        return windows;
    }

    private static async Task<List<Window>> ActivationWindowsAsync(
        IServiceProvider services,
        IReadOnlyCollection<(string ProfileName, DateTime Start, DateTime? End)> spans,
        CancellationToken ct)
    {
        var activations = services.GetRequiredService<IProfileActivationRepository>();
        var windows = new List<Window>();
        foreach (var (_, start, end) in spans)
        {
            var to = end
                ?? (await activations.GetStartingAfterAsync(start, 1, ct)).Select(a => (DateTime?)a.StartTimestamp).FirstOrDefault();
            windows.Add(new Window(start, to));
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
