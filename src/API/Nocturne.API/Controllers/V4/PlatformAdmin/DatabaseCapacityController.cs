using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Platform-admin view of how full the instance's database is and how each tenant uses it.
/// </summary>
/// <remarks>
/// Whole-database figures and other tenants' sizes are for platform administrators only: a tenant's
/// own responses carry only its estimate (<see cref="IStoreSizeReader"/>). The result is cached
/// for the whole platform, not per tenant: it holds sizes only.
/// </remarks>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/database-capacity")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class DatabaseCapacityController(
    ITenantService tenants,
    IStoreSizeReader storeSize,
    IOptions<DbSizeOptions> dbSize,
    IMemoryCache cache) : ControllerBase
{
    private const string CacheKey = "platform:database-capacity";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(DatabaseCapacityDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return ComputeAsync(ct);
        }));

    private async Task<DatabaseCapacityDto> ComputeAsync(CancellationToken ct)
    {
        var database = await storeSize.ReadDatabaseSizeAsync(ct);

        var sizes = new List<TenantStoreSizeDto>();
        foreach (var tenant in await tenants.GetAllAsync(ct))
        {
            var estimate = await storeSize.EstimateAsync(tenant.Id, ct);
            sizes.Add(new TenantStoreSizeDto(
                tenant.Id, tenant.Slug, tenant.DisplayName, estimate.DataSize, estimate.IndexSize));
        }

        var totals = sizes.Select(s => s.DataSizeBytes + s.IndexSizeBytes).ToList();
        var budget = dbSize.Value.MaxBytes;

        return new DatabaseCapacityDto(
            database.TotalBytes,
            database.TotalBytes - database.IndexBytes,
            database.IndexBytes,
            sizes.Count,
            totals.Count > 0 ? (long)totals.Average() : 0,
            totals.Count > 0 ? totals.Max() : 0,
            budget,
            (int)(database.TotalBytes * 100.0 / budget),
            sizes);
    }
}
