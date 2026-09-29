using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once the patient record holds any clinical field the user entered: diabetes type,
/// birth or diagnosis date, sex or pronouns. Every field is optional, so none is singled out. The
/// record itself proves nothing, since the core writes the preferred name and timezone into it.
/// </summary>
public class AboutItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.About;

    public Task<bool> WorksAsync(CancellationToken ct) =>
        db.PatientRecords.AnyAsync(
            r => r.DiabetesType != null
                || r.DateOfBirth != null
                || r.DiagnosisDate != null
                || r.Sex != null
                || r.Pronouns != null,
            ct);
}
