using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once a CGM is on record and the insulin question is answered, by an insulin on record or by
/// "none". Trackers are optional. See <see cref="DeviceSetupService"/>.
/// </summary>
public class DevicesItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Devices;

    public async Task<bool> WorksAsync(CancellationToken ct) =>
        await db.PatientDevices.AnyAsync(d => d.DeviceCategory == nameof(DeviceCategory.CGM), ct)
        && (await db.PatientInsulins.AnyAsync(ct) || await db.PatientRecords.AnyAsync(r => r.TakesNoInsulin, ct));
}
