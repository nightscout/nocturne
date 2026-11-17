using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Offered only to a tenant with no data source, i.e. one that skipped the core's source step.
/// Done once the first reading arrives, not when a source is saved.
/// </summary>
public class ConnectDataItem(INocturneDbContextFactory contexts, IConnectorConfigurationRepository connectors) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.ConnectData;

    public async Task<bool> IsOfferedAsync(CancellationToken ct)
    {
        if (await connectors.AnyAsync(ct))
            return false;

        await using var db = await contexts.CreateAsync(ct);
        return !await db.SensorGlucose.AnyAsync(ct);
    }

    public async Task<bool> WorksAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.SensorGlucose.AnyAsync(ct);
    }
}
