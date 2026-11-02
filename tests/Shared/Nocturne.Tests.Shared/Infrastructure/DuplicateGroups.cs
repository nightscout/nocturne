using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Seeds a duplicate group the way the deduplication module leaves one: a link per record, the
/// copies demoted by <c>DuplicateGroupPrimaries</c>, which flags them <c>is_duplicate</c>. Reads hide
/// a copy by its flag, so a fixture that wrote only the links would show it.
/// </summary>
public static class DuplicateGroups
{
    /// <summary>
    /// Saves <paramref name="context"/>'s pending changes, then links <paramref name="primary"/> and
    /// <paramref name="copies"/> as one group of <paramref name="recordType"/> with every copy demoted.
    /// </summary>
    /// <param name="primary">The group's primary, or null for a group seen only through its copies.</param>
    /// <returns>The group's canonical id.</returns>
    public static async Task<Guid> LinkAsync(
        NocturneDbContext context, RecordType recordType, Guid? primary, params Guid[] copies)
    {
        var canonical = Guid.CreateVersion7();
        var key = RecordTypeKeys.Key(recordType);
        Guid[] members = primary is { } p ? [p, .. copies] : copies;
        context.LinkedRecords.AddRange(members.Select(record => new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            CanonicalId = canonical,
            RecordType = key,
            RecordId = record,
            DataSource = "test",
            IsPrimary = true,
            SysCreatedAt = DateTime.UtcNow,
        }));
        await context.SaveChangesAsync();

        if (!context.Database.IsRelational())
        {
            foreach (var link in context.LinkedRecords.Where(l => l.RecordType == key && copies.Contains(l.RecordId)))
                link.IsPrimary = false;
            await context.SaveChangesAsync();
            await DeriveInMemoryAsync(context, key, recordType, copies);
            return canonical;
        }

        await InTransactionAsync(context, () => DuplicateGroupPrimaries.SetPrimariesAsync(
            context, recordType, copies.Select(id => new PrimaryChange(id, false)).ToList(), default));
        return canonical;
    }

    /// <summary>
    /// Saves <paramref name="context"/>'s pending changes, then sets the flag of each of
    /// <paramref name="records"/> from the links the fixture wrote, for a fixture whose links carry
    /// details of their own (sources, timestamps) that <see cref="LinkAsync"/> does not set.
    /// </summary>
    public static async Task FlagAsync(NocturneDbContext context, RecordType recordType, params Guid[] records)
    {
        await context.SaveChangesAsync();
        if (!context.Database.IsRelational())
        {
            await DeriveInMemoryAsync(context, RecordTypeKeys.Key(recordType), recordType, records);
            return;
        }

        await InTransactionAsync(context, () =>
            DuplicateGroupPrimaries.FlagInsertedLinksAsync(context, recordType, records, default));
    }

    private static async Task InTransactionAsync(NocturneDbContext context, Func<Task> write)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            await write();
            return;
        }

        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await write();
            await transaction.CommitAsync();
        });
    }

    /// <summary>
    /// The in-memory provider runs no bulk statement and the model never saves the flag through a
    /// relational command, so the flag is derived from the links through tracked entities there.
    /// </summary>
    private static async Task DeriveInMemoryAsync(
        NocturneDbContext context, string key, RecordType recordType, Guid[] records)
    {
        var duplicates = await context.LinkedRecords
            .Where(l => l.RecordType == key && !l.IsPrimary && records.Contains(l.RecordId))
            .Select(l => l.RecordId)
            .ToListAsync();
        foreach (var record in await Records(context, recordType).Where(r => records.Contains(r.Id)).ToListAsync())
            record.IsDuplicate = duplicates.Contains(record.Id);
        await context.SaveChangesAsync();
    }

    private static IQueryable<IDuplicateFlagged> Records(NocturneDbContext context, RecordType recordType) =>
        recordType switch
        {
            RecordType.SensorGlucose => context.SensorGlucose.IgnoreQueryFilters(),
            RecordType.Bolus => context.Boluses.IgnoreQueryFilters(),
            RecordType.CarbIntake => context.CarbIntakes.IgnoreQueryFilters(),
            RecordType.BGCheck => context.BGChecks.IgnoreQueryFilters(),
            RecordType.DeviceEvent => context.DeviceEvents.IgnoreQueryFilters(),
            RecordType.Note => context.Notes.IgnoreQueryFilters(),
            RecordType.BolusCalculation => context.BolusCalculations.IgnoreQueryFilters(),
            RecordType.TempBasal => context.TempBasals.IgnoreQueryFilters(),
            RecordType.StateSpan => context.StateSpans.IgnoreQueryFilters(),
            _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null),
        };
}
