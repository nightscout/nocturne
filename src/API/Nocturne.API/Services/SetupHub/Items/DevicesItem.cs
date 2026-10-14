using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once a current CGM is on record and the insulin question is answered, by a current insulin
/// or by "none". Trackers are optional. See <see cref="DeviceSetupService"/>.
/// </summary>
public class DevicesItem(
    NocturneDbContext db,
    IPatientDeviceRepository devices,
    IPatientInsulinRepository insulins) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Devices;

    public async Task<bool> WorksAsync(CancellationToken ct) =>
        (await devices.GetCurrentAsync(ct)).Any(d => d.DeviceCategory == DeviceCategory.CGM)
        && ((await insulins.GetCurrentAsync(ct)).Any() || await db.PatientRecords.AnyAsync(r => r.TakesNoInsulin, ct));
}
