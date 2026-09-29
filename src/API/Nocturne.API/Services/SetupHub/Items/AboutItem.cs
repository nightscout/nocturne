using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once the patient record holds any clinical field the user entered: diabetes type,
/// birth or diagnosis date, sex or pronouns (blank text does not count), or the owner saved the
/// step with all of them left empty (<see cref="ISetupHubService.ConfirmAboutAsync"/>). Every field
/// is optional, so none is singled out. The record itself proves nothing, since the core writes the
/// preferred name and timezone into it.
/// </summary>
public class AboutItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.About;

    public async Task<bool> WorksAsync(CancellationToken ct) =>
        await db.PatientRecords.AnyAsync(
            r => !string.IsNullOrWhiteSpace(r.DiabetesType)
                || r.DateOfBirth != null
                || r.DiagnosisDate != null
                || !string.IsNullOrWhiteSpace(r.Sex)
                || !string.IsNullOrWhiteSpace(r.Pronouns),
            ct)
        || await db.PatientRecords.AnyAsync(
            r => db.SetupHubItems.Any(i => i.ItemKey == Key && i.ConfirmedRecordId == r.Id),
            ct);
}
