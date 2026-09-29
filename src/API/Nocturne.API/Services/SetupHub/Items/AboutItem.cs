using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

public class AboutItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.About;

    public Task<bool> WorksAsync(CancellationToken ct) =>
        db.PatientRecords.AnyAsync(r => r.DiabetesType != null, ct);
}
