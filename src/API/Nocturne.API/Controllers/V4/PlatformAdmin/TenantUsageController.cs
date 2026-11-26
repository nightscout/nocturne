using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Usage metadata for every tenant, for the operator of the instance: when data last arrived, who
/// signs in, connector health, and which features a tenant has set up. It holds UTC dates, counts,
/// configuration and coarse categories only: no time of day, no health value, no error message, no
/// client address or user agent, and no name or email. Needs the instance key.
/// </summary>
/// <remarks>
/// The fields and how each is counted are on <see cref="TenantUsage"/>; the operator guide is
/// <c>guide-operator-usage</c>. A connector is stale once its last successful sync is older than the
/// stale threshold its metadata declares, the one its data source's freshness uses.
/// </remarks>
[ApiController]
[ErrorEnvelope]
[System.ComponentModel.DisplayName("Tenant usage")]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/tenants/usage-summary")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class TenantUsageController(ITenantUsageReader usage, TimeProvider time) : ControllerBase
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;

    /// <summary>
    /// A page of tenants ordered by id, demo tenants left out. Ask for the next page with
    /// <paramref name="offset"/> until a page holds fewer than <paramref name="limit"/> tenants.
    /// </summary>
    /// <param name="tenantId">Only this tenant. An unknown or demo tenant gives an empty list.</param>
    /// <param name="offset">How many tenants to skip.</param>
    /// <param name="limit">At most this many tenants, 1 to 500. The default is 100.</param>
    /// <param name="ct">Cancels the read.</param>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TenantUsageSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(
        [FromQuery] Guid? tenantId,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        if (!IsOperator())
            return Forbid();

        if (offset < 0)
            return Problem(detail: "offset must not be negative.", statusCode: 400, title: "Bad Request");
        if (limit is < 1 or > MaxLimit)
            return Problem(detail: $"limit must be from 1 to {MaxLimit}.", statusCode: 400, title: "Bad Request");

        var generatedAt = time.GetUtcNow().UtcDateTime;
        var tenants = await usage.ReadAsync(tenantId, offset, limit, StaleAfter, ct);
        return Ok(new TenantUsageSummary(generatedAt, tenants));
    }

    private static TimeSpan StaleAfter(string connectorId) => TimeSpan.FromMinutes(
        ConnectorMetadataService.GetByConnectorId(connectorId)?.DefaultStaleThresholdMinutes ?? DefaultStaleMinutes);

    private const int DefaultStaleMinutes = 60;

    /// <summary>The instance key: platform administration with no person behind it.</summary>
    private bool IsOperator() =>
        HttpContext.GetAuthContext() is { IsPlatformAdmin: true, AuthType: AuthType.InstanceKey, SubjectId: null };
}
