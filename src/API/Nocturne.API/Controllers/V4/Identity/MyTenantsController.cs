using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Returns the list of tenants the authenticated user belongs to and allows switching the active tenant context.
/// </summary>
/// <remarks>
/// All tenants for which the authenticated subject has an active membership are returned.
/// Tenant switching writes a new <c>X-Tenant-Slug</c> cookie that is read by subsequent
/// requests' tenant-resolution middleware.
/// </remarks>
/// <seealso cref="ITenantService"/>
[ApiController]
[Route("api/v4/me/tenants")]
[Produces("application/json")]
[Authorize]
public class MyTenantsController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ITenantOverviewService _overviewService;
    private readonly OperatorConfiguration _config;

    public MyTenantsController(
        ITenantService tenantService,
        ITenantOverviewService overviewService,
        IOptions<OperatorConfiguration> config)
    {
        _tenantService = tenantService;
        _overviewService = overviewService;
        _config = config.Value;
    }

    /// <inheritdoc cref="ITenantService.GetTenantsForSubjectAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyTenants(CancellationToken ct)
    {
        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        if (authContext?.SubjectId == null)
            return Unauthorized();

        var tenants = await _tenantService.GetTenantsForSubjectAsync(authContext.SubjectId.Value, ct);
        return Ok(tenants);
    }

    /// <inheritdoc cref="ITenantOverviewService.GetOverviewAsync"/>
    [HttpGet("overview")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantOverviewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantOverviewResponse>> GetOverview(CancellationToken ct)
    {
        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        if (authContext?.SubjectId == null)
            return Unauthorized();

        // This endpoint is tenantless, so MemberScopeMiddleware has not applied the membership
        // restriction — the service resolves it per tenant through MemberScopeResolver, which needs
        // the credential type to know whether the token's scopes are a ceiling at all.
        // InstanceKey/PlatformAccess are superuser auth types, matching MemberScopeMiddleware's
        // handling: they never reach membership resolution there, so stand in a full-access grant.
        IReadOnlySet<string> tokenScopes =
            authContext.AuthType is AuthType.InstanceKey or AuthType.PlatformAccess
                ? new HashSet<string> { Scope.FullAccess }
                : HttpContext.GetGrantedScopes();

        var overview = await _overviewService.GetOverviewAsync(
            authContext.SubjectId.Value, tokenScopes, authContext.AuthType, ct);
        return Ok(overview);
    }

    /// <inheritdoc cref="ITenantService.CreateAsync"/>
    [HttpPost]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetMyTenants"])]
    [ProducesResponseType(typeof(TenantCreatedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateTenant(
        [FromBody] CreateMyTenantRequest request, CancellationToken ct)
    {
        if (!_config.AllowSelfServiceCreation)
            return Forbid();

        var authContext = HttpContext.Items["AuthContext"] as AuthContext;
        if (authContext?.SubjectId == null)
            return Unauthorized();

        var validation = await _tenantService.ValidateSlugAsync(request.Slug, ct);
        if (!validation.IsValid)
            return Problem(detail: validation.Message, statusCode: 400, title: "Bad Request");

        var tenant = await _tenantService.CreateAsync(
            request.Slug, request.DisplayName, authContext.SubjectId.Value, ct);

        return Created($"/api/v4/me/tenants", tenant);
    }

    /// <inheritdoc cref="ITenantService.ValidateSlugAsync"/>
    [HttpGet("validate-slug")]
    [AllowAnonymous]
    [AnonymousUntilSetupComplete]
    [DenyDemoSubject]
    [AllowDuringSetup]
    [EnableRateLimiting("name-availability")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SlugValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ValidateSlug([FromQuery] string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Ok(new SlugValidationResult(false, "Slug is required"));

        var result = await _tenantService.ValidateSlugAsync(slug, ct);
        return Ok(result);
    }
}

public record CreateMyTenantRequest(string Slug, string DisplayName);
