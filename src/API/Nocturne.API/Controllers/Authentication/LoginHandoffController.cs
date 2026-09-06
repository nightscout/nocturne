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
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.Authentication;

/// <summary>
/// Exchanges a login code minted by <see cref="TenantController.IssueLoginCode"/> for a session on
/// the tenant host the code belongs to.
/// </summary>
/// <remarks>
/// The request-scoped context is pinned to the tenant the host resolved to, and a login code is
/// tenant-scoped, so a code minted for one tenant is not visible here on another's host and fails
/// like any unknown code.
/// </remarks>
[ApiController]
[Route("api/auth/handoff")]
[Tags("Authentication")]
[AllowAnonymous]
public class LoginHandoffController : ControllerBase
{
    private readonly NocturneDbContext _dbContext;
    private readonly ILoginCodeService _loginCodeService;
    private readonly ISessionService _sessionService;
    private readonly OidcOptions _oidcOptions;
    private readonly BaseDomainOptions _baseDomain;

    public LoginHandoffController(
        NocturneDbContext dbContext,
        ILoginCodeService loginCodeService,
        ISessionService sessionService,
        IOptions<OidcOptions> oidcOptions,
        IOptions<BaseDomainOptions> baseDomainOptions)
    {
        _dbContext = dbContext;
        _loginCodeService = loginCodeService;
        _sessionService = sessionService;
        _oidcOptions = oidcOptions.Value;
        _baseDomain = baseDomainOptions.Value;
    }

    /// <summary>
    /// Redeems a login code, issues the session cookies, and answers with where to go next.
    /// </summary>
    /// <remarks>
    /// Expired, already-redeemed, unknown and other-tenant codes all fail identically: telling a
    /// caller which one it was would tell them which codes exist.
    /// </remarks>
    [HttpPost]
    [EnableRateLimiting("login-handoff")]
    [RemoteCommand]
    [ProducesResponseType(typeof(LoginHandoffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LoginHandoffResponse>> ExchangeLoginCode(
        [FromBody] LoginHandoffRequest request, CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var subjectId = await _loginCodeService.RedeemAsync(
            _dbContext, request.Code, ipAddress, userAgent, ct);

        if (subjectId is null)
        {
            return Problem(
                detail: "This sign-in link has expired or was already used.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");
        }

        var session = await _sessionService.IssueSessionAsync(
            subjectId.Value,
            new SessionContext(
                DeviceDescription: "Login handoff", IpAddress: ipAddress, UserAgent: userAgent),
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
