using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.SetupHub;

/// <inheritdoc />
/// <remarks>
/// The profile reviewed is the one the profile page opens on: the default row, else the newest
/// document row, with each schedule the newest for its name. A profile is
/// <see cref="TherapySource.Imported"/> when its settings row was created while a migration run of
/// this tenant was going, so a newer upload from an app reads as <see cref="TherapySource.Synced"/>.
/// </remarks>
public partial class TherapySetupService(
    NocturneDbContext db,
    ITherapySettingsRepository settingsRepo,
    IBasalScheduleRepository basalRepo,
    ICarbRatioScheduleRepository carbRatioRepo,
    ISensitivityScheduleRepository sensitivityRepo,
    ITargetRangeScheduleRepository targetRangeRepo,
    IPatientRecordRepository patientRecords) : ITherapySetupService
{
    /// <summary>
    /// Stored numbers are mg/dL whatever the owner typed, and the units label says what they mean;
    /// an "mmol" label over mg/dL numbers is the mislabelling the legacy profile read suffers.
    /// </summary>
    private const string StoredUnits = "mg/dL";

    public async Task<TherapyReview> GetReviewAsync(CancellationToken ct)
    {
        var settings = (await settingsRepo.GetDefaultsAsync(ct)).FirstOrDefault()
            ?? await settingsRepo.GetNewestDocumentRowAsync(ct);
        if (settings is null)
            return new TherapyReview(TherapySource.None, null, null, false, null, null, null, null, null,
                TherapyUnitPlausibility.Rules);

        var now = DateTime.UtcNow;
        var name = settings.ProfileName;
        var basal = await basalRepo.GetActiveAtAsync(name, now, ct);
        var carbRatio = await carbRatioRepo.GetActiveAtAsync(name, now, ct);
        var sensitivity = await sensitivityRepo.GetActiveAtAsync(name, now, ct);
        var targetRange = await targetRangeRepo.GetActiveAtAsync(name, now, ct);

        var source = await SourceOfAsync(settings, ct);
        var confirmedId = await db.SetupHubItems.AsNoTracking()
            .Where(i => i.ItemKey == SetupHubItemKey.Therapy)
            .Select(i => i.ConfirmedRecordId)
            .FirstOrDefaultAsync(ct);
        var lastUpdated = new DateTime?[]
            { settings.Timestamp, basal?.Timestamp, carbRatio?.Timestamp, sensitivity?.Timestamp, targetRange?.Timestamp }
            .Max();

        return new TherapyReview(
            source,
            source == TherapySource.Synced ? settings.EnteredBy ?? settings.Device : null,
            lastUpdated,
            confirmedId == settings.Id,
            settings, basal, carbRatio, sensitivity, targetRange,
            TherapyUnitPlausibility.Rules);
    }

    public async Task<TherapyReview> ConfirmAsync(CancellationToken ct)
    {
        var review = await GetReviewAsync(ct);
        if (review.Source is not (TherapySource.Synced or TherapySource.Imported))
            throw new InvalidOperationException("There is no synced or imported profile to confirm.");
        if (review.Confirmed)
            return review;

        var item = await db.SetupHubItems.FirstOrDefaultAsync(i => i.ItemKey == SetupHubItemKey.Therapy, ct);
        if (item is null)
        {
            item = new SetupHubItemEntity { Id = Guid.CreateVersion7(), ItemKey = SetupHubItemKey.Therapy };
            db.SetupHubItems.Add(item);
        }
        item.ConfirmedRecordId = review.Settings!.Id;
        await db.SaveChangesAsync(ct);
        return review with { Confirmed = true };
    }

    public async Task<TherapyReview> EnterAsync(
        string glucoseUnits,
        IReadOnlyList<TherapyEntryInput> basal,
        IReadOnlyList<TherapyEntryInput> carbRatio,
        IReadOnlyList<TherapyEntryInput> sensitivity,
        IReadOnlyList<TargetEntryInput> targetRange,
        CancellationToken ct)
    {
        var basalValues = Filled(basal, "basal rates");
        var carbRatioValues = Filled(carbRatio, "carb ratios");
        var sensitivityValues = Filled(sensitivity, "insulin sensitivity");
        var targetValues = FilledTargets(targetRange);
        if (basalValues is null && carbRatioValues is null && sensitivityValues is null && targetValues is null)
            throw new ArgumentException("Enter at least one schedule.");

        if ((await GetReviewAsync(ct)).Source != TherapySource.None)
            throw new InvalidOperationException("A therapy profile already exists.");

        var now = DateTime.UtcNow;
        var correlationId = Guid.CreateVersion7();
        T Stamp<T>(T record) where T : V4RecordBase
        {
            record.Timestamp = now;
            record.DataSource = DataSources.ManualEntry;
            record.CorrelationId = correlationId;
            return record;
        }

        var created = await settingsRepo.CreateAsync(Stamp(new TherapySettings
        {
            Units = StoredUnits,
            Timezone = (await patientRecords.GetAsync(ct))?.Timezone,
        }), WriteOrigin.Live, ct);
        await settingsRepo.SetDefaultAsync(created.Id, ct);

        if (basalValues is not null)
            await basalRepo.CreateAsync(Stamp(new BasalSchedule
                { Entries = ProfileDecomposer.ConvertTimeValues(basalValues) }), WriteOrigin.Live, ct);
        if (carbRatioValues is not null)
            await carbRatioRepo.CreateAsync(Stamp(new CarbRatioSchedule
                { Entries = ProfileDecomposer.ConvertTimeValues(carbRatioValues) }), WriteOrigin.Live, ct);
        if (sensitivityValues is not null)
            await sensitivityRepo.CreateAsync(Stamp(new SensitivitySchedule
                { Entries = ProfileDecomposer.ConvertSensitivityValues(sensitivityValues, glucoseUnits) }), WriteOrigin.Live, ct);
        if (targetValues is { } targets)
            await targetRangeRepo.CreateAsync(Stamp(new TargetRangeSchedule
                { Entries = ProfileDecomposer.MergeTargets(targets.Lows, targets.Highs, glucoseUnits) }), WriteOrigin.Live, ct);

        return await GetReviewAsync(ct);
    }

    private async Task<TherapySource> SourceOfAsync(TherapySettings settings, CancellationToken ct)
    {
        if (settings.DataSource == DataSources.ManualEntry)
            return TherapySource.Entered;
        if (settings.IsExternallyManaged)
            return TherapySource.Synced;

        var createdAt = await db.TherapySettings.AsNoTracking()
            .Where(t => t.Id == settings.Id)
            .Select(t => t.SysCreatedAt)
            .FirstAsync(ct);
        var imported = await db.MigrationRuns.AsNoTracking().AnyAsync(r =>
            r.TenantId == db.TenantId
            && r.StartedAt <= createdAt
            && (r.CompletedAt == null || createdAt <= r.CompletedAt), ct);
        return imported ? TherapySource.Imported : TherapySource.Synced;
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeOfDay();

    /// <summary>The schedule's blocks, or null when every value was left blank.</summary>
    private static List<TimeValue>? Filled(IReadOnlyList<TherapyEntryInput> entries, string schedule)
    {
        if (entries.All(e => e.Value is null))
            return null;
        CheckTimes(entries.Select(e => e.Time).ToList(), schedule);
        if (entries.Any(e => e.Value is not (> 0 and < double.PositiveInfinity)))
            throw new ArgumentException($"Every time block of the {schedule} needs a value above zero.");

        return entries.OrderBy(e => e.Time, StringComparer.Ordinal)
            .Select(e => new TimeValue { Time = e.Time, Value = e.Value!.Value })
            .ToList();
    }

    private static (List<TimeValue> Lows, List<TimeValue> Highs)? FilledTargets(IReadOnlyList<TargetEntryInput> entries)
    {
        if (entries.All(e => e.Low is null && e.High is null))
            return null;
        CheckTimes(entries.Select(e => e.Time).ToList(), "target range");
        if (entries.Any(e => e.Low is not (> 0 and < double.PositiveInfinity) || e.High is not (> 0 and < double.PositiveInfinity)))
            throw new ArgumentException("Every time block of the target range needs a low and a high above zero.");
        if (entries.Any(e => e.Low > e.High))
            throw new ArgumentException("Each low target must not be above its high target.");

        var ordered = entries.OrderBy(e => e.Time, StringComparer.Ordinal).ToList();
        return (ordered.Select(e => new TimeValue { Time = e.Time, Value = e.Low!.Value }).ToList(),
            ordered.Select(e => new TimeValue { Time = e.Time, Value = e.High!.Value }).ToList());
    }

    private static void CheckTimes(List<string> times, string schedule)
    {
        if (times.Any(t => !TimeOfDay().IsMatch(t)))
            throw new ArgumentException($"Every time block of the {schedule} needs a start time.");
        if (times.Distinct().Count() != times.Count)
            throw new ArgumentException($"The {schedule} has two time blocks starting at the same time.");
        if (!times.Contains("00:00"))
            throw new ArgumentException($"The {schedule} needs a time block starting at midnight (00:00).");
    }
}
