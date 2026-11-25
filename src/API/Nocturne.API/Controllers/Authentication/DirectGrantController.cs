using Nocturne.API.Authorization;
using Nocturne.API.Attributes;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Programmatic API tokens for scripts and automation, issued without an OAuth client. A caller can create,
/// list and delete direct grant tokens. Tokens use the <c>noc_</c> prefix.
/// </summary>
/// <remarks>
/// Tokens are validated by <see cref="DirectGrantTokenHandler"/>.
///
/// Direct grants are bearer tokens tied to a specific user but issued outside the standard
/// OAuth 2.0 consent flow. They are intended for scripts, automation, and server-to-server
/// integrations where launching an authorization-code flow is impractical.
///
/// Token generation uses <see cref="System.Security.Cryptography.RandomNumberGenerator"/> to produce
/// 32 bytes of entropy encoded as a Base64-URL string. Only the SHA-256 hash of the token is stored
/// (<see cref="Connectors.Core.Utilities.HashUtils.Sha256Hex"/>); the plaintext is returned once at
/// creation and cannot be retrieved again.
///
/// Scopes are capped at the caller's own (<see cref="Scope.ValidateDelegation"/>) and normalized via
/// <see cref="Scope.Normalize"/> before storage.
/// All mutations are audit-logged through <see cref="IAuthAuditService"/>.
/// </remarks>
/// <seealso cref="DirectGrantTokenHandler"/>
/// <seealso cref="IDirectGrantService"/>
/// <seealso cref="Scope"/>
[ApiController]
[Route("api/auth/direct-grants")]
[RequireInteractiveSession]
[System.ComponentModel.DisplayName("API Tokens")]
[Tags("Authentication")]
public class DirectGrantController : ControllerBase
{
    private readonly ISubjectRepository _subjects;
    private readonly IDirectGrantService _directGrantService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectGrantController"/> class.
    /// </summary>
    /// <param name="subjects">The repository that finds the tenant's device subject, the holder of its site tokens.</param>
    /// <param name="directGrantService">The service that creates, lists, and revokes direct grants.</param>
    public DirectGrantController(
        ISubjectRepository subjects,
        IDirectGrantService directGrantService)
    {
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _directGrantService = directGrantService;
    }

    /// <summary>
    /// Create a new direct grant token. The plaintext token is returned once and cannot be retrieved again.
    /// </summary>
    /// <param name="request">The create request containing the human-readable label, desired scopes, and optional expiry.</param>
    /// <returns>A <see cref="CreateDirectGrantResponse"/> containing the grant ID and the single-use plaintext token.</returns>
    [HttpPost]
    [RemoteCommand(Invalidates = ["List", "Services_GetConnections"])]
    [ProducesResponseType(typeof(CreateDirectGrantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CreateDirectGrantResponse>> Create([FromBody] CreateDirectGrantRequest request)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        if (await HttpContext.IsCallerInactiveAsync(HttpContext.RequestAborted))
        {
            return Challenge();
        }

        if (Scope.ValidateDelegation(request.Scopes, HttpContext.GetGrantedScopes()) is { } violation)
        {
            return Problem(detail: violation.Description, statusCode: 403, title: violation.Code);
        }

        var limitTo24Hours = HttpContext.InheritOwnCredentialHistoryClamp(request.LimitTo24Hours);

        var result = await _directGrantService.CreateAsync(
            pinnedTenantId: null, auth.SubjectId.Value, request.Label, request.Scopes, request.ExpiresAt,
            limitTo24Hours,
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent.ToString(),
            createdBySubjectId: null,
            ct: HttpContext.RequestAborted);

        if (result.Error != null)
        {
            return Problem(detail: result.Error, statusCode: 400, title: "Bad Request");
        }

        return Ok(result.Response);
    }

    /// <summary>
    /// List all active direct grants for the authenticated user.
    /// Never returns the token itself.
    /// </summary>
    /// <returns>A list of <see cref="DirectGrantDto"/> objects representing non-revoked grants for the current user.</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DirectGrantDto>), StatusCodes.Status200OK)]
    [ScopeDecidedBy(typeof(DirectGrantController), nameof(SiteTokenHolderAsync))]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<DirectGrantDto>>> List()
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var grants = await _directGrantService.ListAsync(
            pinnedTenantId: null, auth.SubjectId.Value, HttpContext.RequestAborted);

        // An imported token authenticates as the holder itself, so a caller can be the holder and
        // would otherwise see every site token twice.
        foreach (var holderId in await SiteTokenHolderAsync())
        {
            if (holderId == auth.SubjectId.Value)
            {
                continue;
            }

            grants.AddRange(await _directGrantService.ListAsync(
                pinnedTenantId: null, holderId, HttpContext.RequestAborted));
        }

        return Ok(grants);
    }

    /// <summary>
    /// Revoke a direct grant by setting its <c>RevokedAt</c> timestamp. This operation is idempotent.
    /// </summary>
    /// <param name="id">The GUID of the grant to revoke.</param>
    /// <returns><c>204 No Content</c> on success (including when already revoked); <c>404 Not Found</c> if the grant does not belong to the current user.</returns>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["List", "Services_GetConnections"])]
    [ScopeDecidedBy(typeof(DirectGrantController), nameof(SiteTokenHolderAsync))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth == null || !auth.IsAuthenticated || auth.SubjectId == null)
        {
            return Problem(detail: "Authentication required", statusCode: 401, title: "Unauthorized");
        }

        var found = await _directGrantService.RevokeAsync(
            pinnedTenantId: null, id, auth.SubjectId.Value,
            HttpContext.GetClientAddress(),
            Request.Headers.UserAgent.ToString(),
            ct: HttpContext.RequestAborted);

        foreach (var holderId in await SiteTokenHolderAsync())
        {
            if (found || holderId == auth.SubjectId.Value)
            {
                continue;
            }

            found = await _directGrantService.RevokeAsync(
                pinnedTenantId: null, id, holderId,
                HttpContext.GetClientAddress(),
                Request.Headers.UserAgent.ToString(),
                ct: HttpContext.RequestAborted);
        }

        if (!found)
        {
            return Problem(detail: "Direct grant not found", statusCode: 404, title: "Not Found");
        }

        return NoContent();
    }

    /// <summary>
    /// Remove the expiry of a device credential that a member set up, so it does not stop when that
    /// member's access ends.
    /// </summary>
    /// <remarks>
    /// Keeping a credential gives the site's uploader access with no end, so it takes a caller who
    /// manages the members and whose own access does not end
    /// (<see cref="HttpContextExtensions.MayKeepDeviceCredentials"/>).
    /// </remarks>
    /// <param name="id">The device credential, as the connections list shows it.</param>
    /// <returns><c>204 No Content</c> on success; <c>404 Not Found</c> when it is not a device credential.</returns>
    [HttpPost("{id:guid}/keep")]
    [RequireScope(Scope.MembersManage)]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["List", "Services_GetConnections"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Keep(Guid id)
    {
        if (await HttpContext.IsCallerInactiveAsync(HttpContext.RequestAborted))
        {
            return Challenge();
        }

        if (HttpContext.CallerGrantCeiling() is not { } ceiling)
        {
            return this.CallerAccessFromTeamProblem();
        }

        if (!HttpContext.MayKeepDeviceCredentials())
        {
            return ceiling.AccessEndsAt is null
                ? Forbid()
                : Problem(detail: HttpContextExtensions.KeepDeviceCredentialEndDateDetail, statusCode: 403, title: "Forbidden");
        }

        return await _directGrantService.KeepDeviceCredentialAsync(id, HttpContext.RequestAborted)
            ? NoContent()
            : Problem(detail: "No device credential has this id.", statusCode: 404, title: "Not Found");
    }

    /// <summary>
    /// The tenant's device-token holder, when the caller may manage it, and nothing otherwise.
    /// </summary>
    /// <remarks>
    /// A site's uploader tokens belong to nobody (see <see cref="ISubjectRepository.FindDeviceSubjectAsync"/>), so no
    /// person's own list would ever contain them. Revoking a device is the operation someone
    /// reaches for when a phone is lost, so it cannot be the one thing this screen cannot do.
    /// Gated on <see cref="Scope.MembersManage"/> rather than shown to every member, because a
    /// site's tokens are not the business of everyone who can read its data.
    /// </remarks>
    private async Task<IEnumerable<Guid>> SiteTokenHolderAsync()
    {
        if (!HttpContext.HasScope(Scope.MembersManage))
        {
            return [];
        }

        return await _subjects.FindDeviceSubjectAsync(HttpContext.RequestAborted) is { } holderId
            ? [holderId]
            : [];
    }
}

#region Request/Response DTOs

/// <summary>
/// Request to create a new direct grant
/// </summary>
public class CreateDirectGrantRequest
{
    public string Label { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Whether the grant may read only the last 24 hours of time-series data.
    /// </summary>
    public bool LimitTo24Hours { get; set; }
}

#endregion
