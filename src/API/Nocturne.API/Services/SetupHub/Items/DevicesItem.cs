using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once a current CGM is on record and the insulin question is answered, by a current insulin
/// or by "none". Trackers are optional. See <see cref="DeviceSetupService"/>.
/// </summary>
public class DevicesItem(
    INocturneDbContextFactory contexts,
    IPatientDeviceRepository devices,
    IPatientInsulinRepository insulins) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Devices;

    public async Task<bool> WorksAsync(CancellationToken ct)
    {
        if (!(await devices.GetCurrentAsync(ct)).Any(d => d.DeviceCategory == DeviceCategory.CGM))
            return false;
        if ((await insulins.GetCurrentAsync(ct)).Any())
            return true;

        await using var db = await contexts.CreateAsync(ct);
        return await db.PatientRecords.AnyAsync(r => r.TakesNoInsulin, ct);
    }
}
