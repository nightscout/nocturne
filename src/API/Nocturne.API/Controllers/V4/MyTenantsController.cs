using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4;

[ApiController]
[Route("api/v4/me/tenants")]
[Produces("application/json")]
[Authorize]
public class MyTenantsController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly MultitenancyConfiguration _config;

    public MyTenantsController(
        ITenantService tenantService,
        IOptions<MultitenancyConfiguration> config)
    {
        _tenantService = tenantService;
        _config = config.Value;
    }

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

    [HttpPost]
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
            request.Slug, request.DisplayName, authContext.SubjectId.Value, request.ApiSecret, ct);

        return Created($"/api/v4/me/tenants", tenant);
    }

    [HttpGet("validate-slug")]
    [AllowAnonymous]
    [AllowDuringSetup]
    [RemoteQuery]
    [ProducesResponseType(typeof(SlugValidationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateSlug([FromQuery] string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Ok(new SlugValidationResult(false, "Slug is required"));

        var result = await _tenantService.ValidateSlugAsync(slug, ct);
        return Ok(result);
    }
}

public record CreateMyTenantRequest(string Slug, string DisplayName, string? ApiSecret = null);
