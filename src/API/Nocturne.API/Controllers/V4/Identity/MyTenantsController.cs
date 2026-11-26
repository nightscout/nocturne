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
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The tenants the signed-in account belongs to. A caller can list them, read an overview of each,
/// and check whether a slug is available. A caller can create a tenant when the operator allows
/// self-service creation.
/// </summary>
/// <remarks>
/// All tenants for which the authenticated subject has an active membership are returned.
/// </remarks>
/// <seealso cref="ITenantService"/>
[ApiController]
[Tags("Identity")]
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

    /// <summary>
    /// Resolves the subject the caller answers for, or the response to send in its place.
    /// Three credentials authenticate without a subject of their own: a guest session, whose grant
    /// records the data owner instead; the instance key, a service credential; and development
    /// auto-authentication. None of them is a member of any tenant, so each gets
    /// <paramref name="noMemberships"/> rather than a rejection — a 401 reads as an expired
    /// credential and bounces a live session to the login page.
    /// </summary>
    private ActionResult? ResolveOwnSubject<T>(
        T noMemberships, out AuthContext caller, out Guid subjectId)
    {
        caller = HttpContext.GetAuthContext() ?? AuthContext.Unauthenticated();
        subjectId = Guid.Empty;

        if (!caller.IsAuthenticated)
            return Unauthorized();

        if (caller.SubjectId is not { } id)
            return Ok(noMemberships);

        subjectId = id;
        return null;
    }

    /// <summary>
    /// Returns every tenant the caller belongs to, or with <paramref name="reachableOnly"/> only
    /// those they can go to.
    /// </summary>
    /// <param name="reachableOnly">Leaves out inactive tenants.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <seealso cref="ITenantService.GetTenantsForSubjectAsync"/>
    /// <seealso cref="ITenantService.GetReachableTenantsForSubjectAsync"/>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<TenantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyTenants(
        [FromQuery] bool reachableOnly = false, CancellationToken ct = default)
    {
        if (ResolveOwnSubject(new List<TenantDto>(), out _, out var subjectId) is { } answer)
            return answer;

        var tenants = reachableOnly
            ? await _tenantService.GetReachableTenantsForSubjectAsync(subjectId, ct)
            : await _tenantService.GetTenantsForSubjectAsync(subjectId, ct);
        return Ok(tenants);
    }

    /// <summary>
    /// Returns every person the caller may read glucose for, as <see cref="ITenantOverviewService.GetOverviewAsync"/>
    /// orders them, or with <paramref name="group"/> only that group's
    /// (<see cref="ITenantOverviewService.GetGroupOverviewAsync"/>).
    /// </summary>
    /// <param name="group">One of the caller's groups (<see cref="GetGroups"/>); 404 for any other.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("overview")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantOverviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantOverviewResponse>> GetOverview(
        [FromQuery] Guid? group, CancellationToken ct)
    {
        var caller = HttpContext.GetAuthContext() ?? AuthContext.Unauthenticated();
        if (!caller.IsAuthenticated)
            return Unauthorized();

        if (group is { } groupId)
            return caller.SubjectId is { } member
                && await _overviewService.GetGroupOverviewAsync(
                    member, groupId, TokenScopesOf(caller), caller.AuthType, caller.LimitTo24Hours, ct)
                    is { } groupOverview
                ? Ok(groupOverview)
                : NoSuchGroup();

        // See ResolveOwnSubject for why a caller without a subject is answered, not refused.
        if (caller.SubjectId is not { } subjectId)
            return Ok(new TenantOverviewResponse([], null));

        var overview = await _overviewService.GetOverviewAsync(
            subjectId, TokenScopesOf(caller), caller.AuthType, caller.LimitTo24Hours, ct);
        return Ok(overview);
    }

    /// <inheritdoc cref="ITenantOverviewService.GetGroupsAsync"/>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("overview/groups")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<StaffGroup>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGroups(CancellationToken ct)
    {
        if (ResolveOwnSubject(new List<StaffGroup>(), out _, out var subjectId) is { } answer)
            return answer;

        return Ok(await _overviewService.GetGroupsAsync(subjectId, ct));
    }

    /// <summary>
    /// Returns the period statistics of every person <see cref="GetOverview"/> lists for the same
    /// <paramref name="group"/> (<see cref="ITenantOverviewService.GetReviewAsync"/>).
    /// </summary>
    /// <param name="group">One of the caller's groups (<see cref="GetGroups"/>); 404 for any other.</param>
    /// <param name="days">The length of the period: 14, 30 or 90.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("overview/review")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantReviewResponse>> GetReview(
        [FromQuery] Guid? group, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        // A caller without a subject is answered by the service, which states the periods: see
        // ResolveOwnSubject for why it is not refused.
        var caller = HttpContext.GetAuthContext() ?? AuthContext.Unauthenticated();
        if (!caller.IsAuthenticated)
            return Unauthorized();

        if (!TenantReviewPeriods.IsSupported(days))
            return Problem(
                detail: $"The period must be one of {string.Join(", ", TenantReviewPeriods.Days)} days.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");

        if (group is { } groupId)
            return caller.SubjectId is { } subjectId
                && await _overviewService.GetGroupReviewAsync(
                    subjectId, groupId, TokenScopesOf(caller), caller.AuthType, days, caller.LimitTo24Hours, ct)
                    is { } groupReview
                ? Ok(groupReview)
                : NoSuchGroup();

        var review = await _overviewService.GetReviewAsync(
            caller.SubjectId, TokenScopesOf(caller), caller.AuthType, days, caller.LimitTo24Hours, ct);
        return Ok(review);
    }

    /// <summary>A group the caller is not in as current staff reads as one that does not exist.</summary>
    private ObjectResult NoSuchGroup() =>
        Problem(detail: "No such group.", statusCode: StatusCodes.Status404NotFound, title: "Not Found");

    /// <inheritdoc cref="ITenantOverviewService.GetPersonAsync"/>
    /// <param name="tenantId">The person's tenant.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("overview/person")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantPersonDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantPersonDetail>> GetPerson(
        [FromQuery] Guid tenantId, CancellationToken ct = default)
    {
        var caller = HttpContext.GetAuthContext() ?? AuthContext.Unauthenticated();
        if (!caller.IsAuthenticated)
            return Unauthorized();

        if (tenantId == Guid.Empty)
            return Problem(
                detail: "A tenant id is required.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");

        var person = await _overviewService.GetPersonAsync(
            caller.SubjectId, tenantId, TokenScopesOf(caller), caller.AuthType, caller.LimitTo24Hours, ct);
        return person is null
            ? Problem(
                detail: "No such person.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Not Found")
            : Ok(person);
    }

    /// <summary>
    /// The scopes the credential's grant allows. These endpoints are tenantless, so
    /// MemberScopeMiddleware has not applied the membership restriction: the service resolves it
    /// per tenant through MemberScopeResolver, which needs the credential type to know whether the
    /// token's scopes are a ceiling at all. PlatformAccess is a superuser auth type, matching
    /// MemberScopeMiddleware's handling: it never reaches membership resolution there, so stand in
    /// a full-access grant.
    /// </summary>
    private IReadOnlySet<string> TokenScopesOf(AuthContext caller) =>
        caller.AuthType is AuthType.PlatformAccess
            ? new HashSet<string> { Scope.FullAccess }
            : HttpContext.GetGrantedScopes();

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

        var authContext = HttpContext.GetAuthContext();
        if (authContext?.SubjectId == null)
            return Unauthorized();

        var validation = await _tenantService.ValidateSlugAsync(request.Slug, ct);
        if (!validation.IsValid)
            return Problem(detail: validation.Message, statusCode: 400, title: "Bad Request");

        var tenant = await _tenantService.CreateAsync(
            request.Slug, request.DisplayName, authContext.SubjectId.Value, ct);

        return Created($"/api/v4/me/tenants", tenant);
    }

    /// <inheritdoc cref="ITenantService.ValidateSlugAsync(string, CancellationToken)"/>
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
