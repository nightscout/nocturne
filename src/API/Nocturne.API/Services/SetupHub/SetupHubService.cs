using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// Settles the setup hub on every read: an item is listed once <see cref="ISetupHubItem.IsOfferedAsync"/>
/// says so and stays listed, and turns <see cref="SetupHubItemState.Done"/> for good once
/// <see cref="ISetupHubItem.WorksAsync"/> says so. Nothing a read or a revisit does can reopen an
/// item; only the owner reopening one they set aside can.
/// </summary>
public class SetupHubService : ISetupHubService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IReadOnlyList<ISetupHubItem> _items;

    public SetupHubService(INocturneDbContextFactory contexts, IEnumerable<ISetupHubItem> items)
    {
        _contexts = contexts;
        _items = items.OrderBy(i => i.Key).ToList();
    }

    public async Task<SetupHubStatus> GetAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await DescribeAsync(db, await SettleAsync(db, ct), ct);
    }

    public async Task<SetupHubStatus> SetStateAsync(
        SetupHubItemKey key, SetupHubItemState state, CancellationToken ct)
    {
        if (state == SetupHubItemState.Done)
            throw new ArgumentException("Done is reached by the item working, never set.", nameof(state));

        await using var db = await _contexts.CreateAsync(ct);
        var rows = await SettleAsync(db, ct);
        var row = rows.FirstOrDefault(r => r.ItemKey == key)
            ?? throw new KeyNotFoundException($"{key} is not on this tenant's setup hub.");
        if (row.State == SetupHubItemState.Done)
            throw new InvalidOperationException($"{key} is done.");

        if (row.State != state)
        {
            row.State = state;
            await db.SaveChangesAsync(ct);
        }

        return await DescribeAsync(db, rows, ct);
    }

    public async Task<SetupHubStatus> ConfirmAboutAsync(CancellationToken ct)
    {
        await using (var db = await _contexts.CreateAsync(ct))
        {
            var recordId = await db.PatientRecords.AsNoTracking().Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("There is no patient record to confirm.");

            var rows = await SettleAsync(db, ct);
            var row = rows.FirstOrDefault(r => r.ItemKey == SetupHubItemKey.About)
                ?? throw new KeyNotFoundException("About is not on this tenant's setup hub.");
            if (row.ConfirmedRecordId != recordId)
            {
                row.ConfirmedRecordId = recordId;
                await db.SaveChangesAsync(ct);
            }
        }

        return await GetAsync(ct);
    }

    public async Task<SetupHubStatus> DismissStripAsync(string revision, CancellationToken ct)
    {
        await using (var db = await _contexts.CreateAsync(ct))
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == db.TenantId, ct);
            tenant.SetupStripDismissedRevision = revision;
            await db.SaveChangesAsync(ct);
        }

        return await GetAsync(ct);
    }

    private async Task<List<SetupHubItemEntity>> SettleAsync(
        NocturneDbContext db, CancellationToken ct, bool retry = true)
    {
        var stored = await db.SetupHubItems.ToDictionaryAsync(r => r.ItemKey, ct);
        var listed = new List<SetupHubItemEntity>();

        foreach (var item in _items)
        {
            if (!stored.TryGetValue(item.Key, out var row))
            {
                if (!await item.IsOfferedAsync(ct))
                    continue;
                row = new SetupHubItemEntity { Id = Guid.CreateVersion7(), ItemKey = item.Key };
                db.SetupHubItems.Add(row);
            }

            if (row.State != SetupHubItemState.Done && await item.WorksAsync(ct))
                row.State = SetupHubItemState.Done;

            listed.Add(row);
        }

        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (retry)
            {
                // A concurrent read listed the same item first; its rows are as good as ours.
                db.ChangeTracker.Clear();
                return await SettleAsync(db, ct, retry: false);
            }
        }

        return listed;
    }

    private static async Task<SetupHubStatus> DescribeAsync(
        NocturneDbContext db, List<SetupHubItemEntity> rows, CancellationToken ct)
    {
        var items = rows.Select(r => new SetupHubItem(r.ItemKey, r.State)).ToList();
        var resolved = items.Count(i => i.State != SetupHubItemState.Open);
        var revision = RevisionOf(items);

        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == db.TenantId)
            .Select(t => new { t.SetupHubEnrolledAt, t.SetupStripDismissedRevision })
            .FirstAsync(ct);

        var showStrip = tenant.SetupHubEnrolledAt != null
            && resolved < items.Count
            && tenant.SetupStripDismissedRevision != revision;

        return new SetupHubStatus(items, resolved, items.Count - resolved, items.Count, revision, showStrip);
    }

    private static string RevisionOf(IEnumerable<SetupHubItem> items)
    {
        var text = string.Join('|', items.Select(i => $"{i.Key}:{i.State}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
