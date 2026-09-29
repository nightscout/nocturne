using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once the patient record has a diabetes type: the one field the clinical form requires, so
/// any save of it counts. The record itself proves nothing, since the core writes the preferred
/// name and timezone into it; the other clinical fields are optional because not everyone knows them.
/// </summary>
public class AboutItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.About;

    public Task<bool> WorksAsync(CancellationToken ct) =>
        db.PatientRecords.AnyAsync(r => r.DiabetesType != null, ct);
}
