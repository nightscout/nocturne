using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once the owner confirmed a synced or imported profile matches their app, or entered one by
/// hand (<see cref="TherapySetupService"/>). A profile merely arriving is not enough: nobody has
/// checked it yet.
/// </summary>
public class TherapyItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Therapy;

    public async Task<bool> WorksAsync(CancellationToken ct) =>
        await db.SetupHubItems.AnyAsync(i => i.ItemKey == Key && i.ConfirmedRecordId != null, ct)
        || await db.TherapySettings.AnyAsync(t => t.DataSource == DataSources.ManualEntry, ct);
}
