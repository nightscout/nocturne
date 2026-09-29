using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

public class TherapyItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Therapy;

    public Task<bool> WorksAsync(CancellationToken ct) => db.TherapySettings.AnyAsync(ct);
}
