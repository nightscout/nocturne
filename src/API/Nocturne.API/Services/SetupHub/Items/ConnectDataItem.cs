using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Offered only to a tenant with no data source, i.e. one that skipped the core's source step.
/// Done once the first reading arrives, not when a source is saved.
/// </summary>
public class ConnectDataItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.ConnectData;

    public async Task<bool> IsOfferedAsync(CancellationToken ct) =>
        !await db.ConnectorConfigurations.AnyAsync(ct) && !await WorksAsync(ct);

    public Task<bool> WorksAsync(CancellationToken ct) => db.SensorGlucose.AnyAsync(ct);
}
