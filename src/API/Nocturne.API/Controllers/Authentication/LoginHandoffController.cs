using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Exchanges a login code for a session on the tenant the code belongs to.
/// </summary>
/// <remarks>
/// Login codes are minted by <see cref="TenantController.IssueLoginCode"/>.
///
/// A context from the request's factory reads the tenant the host resolved to, and a login code is
/// tenant-scoped, so a code minted for one tenant is not visible here on another's host and fails
/// like any unknown code.
/// </remarks>
[ApiController]
[Route("api/auth/handoff")]
[Tags("Authentication")]
[AllowAnonymous]
public class LoginHandoffController : ControllerBase
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILoginCodeService _loginCodeService;
    private readonly ISessionService _sessionService;
    private readonly ITenantMemberService _tenantMembers;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly OidcOptions _oidcOptions;
    private readonly BaseDomainOptions _baseDomain;

    public LoginHandoffController(
        INocturneDbContextFactory contexts,
        ILoginCodeService loginCodeService,
        ISessionService sessionService,
        ITenantMemberService tenantMembers,
        ITenantAccessor tenantAccessor,
        IOptions<OidcOptions> oidcOptions,
        IOptions<BaseDomainOptions> baseDomainOptions)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _loginCodeService = loginCodeService;
        _sessionService = sessionService;
        _tenantMembers = tenantMembers;
        _tenantAccessor = tenantAccessor;
        _oidcOptions = oidcOptions.Value;
        _baseDomain = baseDomainOptions.Value;
    }

    /// <summary>
    /// Redeems a login code, issues the session cookies, and answers with where to go next.
    /// Expired, already-redeemed, unknown, other-tenant and no-longer-admitted codes all fail identically.
    /// </summary>
    /// <remarks>
    /// Expired, already-redeemed, unknown, other-tenant and no-longer-admitted codes all fail
    /// identically: telling a caller which one it was would tell them which codes exist. A code is a
    /// bearer link that outlives the moment it was minted, so its member must still be a live member
    /// of the tenant (<see cref="ITenantMemberService.IsMemberAsync"/>) when it is redeemed.
    /// </remarks>
    [HttpPost]
    [EnableRateLimiting("login-handoff")]
    [RemoteCommand]
    [ProducesResponseType(typeof(LoginHandoffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LoginHandoffResponse>> ExchangeLoginCode(
        [FromBody] LoginHandoffRequest request, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var ipAddress = HttpContext.GetClientAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        var tenantId = _tenantAccessor.TenantId;
        var redeemed = await _loginCodeService.RedeemAsync(
            db, request.Code, [LoginCodePurpose.Login],
            admits: LoginCodeAdmission.LiveMemberOf(_tenantMembers, tenantId),
            withSpend: null, ipAddress, userAgent, ct);

        if (redeemed is null)
        {
            return Problem(
                detail: "This sign-in link has expired or was already used.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");
        }

        var session = await _sessionService.IssueSessionAsync(
            redeemed.SubjectId,
            new SessionContext(
                DeviceDescription: SessionContext.LoginHandoffDeviceDescription, IpAddress: ipAddress, UserAgent: userAgent,
                LoginCodeId: redeemed.CodeId),
            ct);

        Response.SetSessionCookies(session, _oidcOptions);

        var returnUrl = request.ReturnUrl is { Length: > 0 } requested
            && _baseDomain.IsValidReturnUrl(requested)
                ? requested
                : "/";

        return Ok(new LoginHandoffResponse(returnUrl));
    }
}

/// <param name="Code">The code minted by the platform-admin login-code endpoint.</param>
/// <param name="ReturnUrl">Where to land after signing in. Anything off this site falls back to <c>/</c>.</param>
public record LoginHandoffRequest(string Code, string? ReturnUrl = null);

/// <param name="ReturnUrl">The validated target the caller should navigate to.</param>
public record LoginHandoffResponse(string ReturnUrl);
