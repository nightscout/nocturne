using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Demo;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Demo;

/// <summary>
/// Signs an anonymous visitor in to the demo tenant as its shared demo member.
/// </summary>
/// <remarks>
/// The public share host (<c>{token}.share.{baseDomain}</c>) can only ever serve a
/// read-only, category-filtered view, and the bare tenant host is login-only — so
/// neither can show a visitor the write or settings surfaces the demo exists to
/// demonstrate. Issuing a real session for the demo tenant's own member instead means
/// the visitor is an ordinary authenticated member, and every authorization, RLS and
/// UI path behaves exactly as it does for a real user, with no demo special cases.
/// <para>
/// Only reachable on a tenant whose <c>IsDemo</c> flag is set, which is set solely by
/// the demo provisioning endpoint. Every other tenant returns 404.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v4/demo")]
[AllowAnonymous]
[AllowDuringSetup]
[ApiExplorerSettings(IgnoreApi = true)]
public class DemoSessionController : ControllerBase
{
    private readonly DemoTenantService _demoTenantService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ISessionService _sessionService;
    private readonly IOptions<OidcOptions> _oidcOptions;
    private readonly ILogger<DemoSessionController> _logger;

    public DemoSessionController(
        DemoTenantService demoTenantService,
        ITenantAccessor tenantAccessor,
        ISessionService sessionService,
        IOptions<OidcOptions> oidcOptions,
        ILogger<DemoSessionController> logger)
    {
        _demoTenantService = demoTenantService;
        _tenantAccessor = tenantAccessor;
        _sessionService = sessionService;
        _oidcOptions = oidcOptions;
        _logger = logger;
    }

    /// <summary>
    /// Issues a demo session and redirects into the app. Navigating a browser here on
    /// the demo tenant's host leaves it signed in as the demo member.
    /// </summary>
    /// <remarks>
    /// A state-changing GET is deliberate: browser navigation from the login page is
    /// the use case, and the login page cannot POST a redirect. The state it changes is
    /// limited to minting a session for the demo tenant's own member, which any caller
    /// can request anyway; it carries no CSRF risk because there is no victim whose
    /// authority is borrowed.
    /// </remarks>
    [HttpGet("session")]
    [EnableRateLimiting("demo-session")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateSession(
        [FromQuery] string? redirect, [FromQuery] string? format, CancellationToken ct)
    {
        var subjectId = await ResolveDemoSubjectAsync(ct);
        if (subjectId is null)
            return NotFound();

        // Anonymous, and each call writes a refresh_tokens row; the ceiling is applied where the
        // row is created rather than here — see DemoSessionLimits.
        //
        // No IP or user-agent: every visitor shares this subject, and the session list at
        // /api/v4/account/sessions is readable by any member of it — recording them would
        // show each visitor the addresses of everyone else currently using the demo.
        var session = await _sessionService.IssueSessionAsync(
            subjectId.Value,
            new SessionContext(DeviceDescription: "demo-visitor"),
            ct);

        Response.SetSessionCookies(session, _oidcOptions.Value);

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new DemoSessionResponse(
                session.AccessToken, session.RefreshToken, session.ExpiresInSeconds));
        }

        var target = redirect != null && Url.IsLocalUrl(redirect) ? redirect : "/";
        return Redirect(target);
    }

    /// <summary>
    /// Resolves the demo member subject for the request's tenant, or
    /// <see langword="null"/> when the request is not eligible for a demo session.
    /// </summary>
    private async Task<Guid?> ResolveDemoSubjectAsync(CancellationToken ct)
    {
        // The share host serves the anonymous read-only view and never honors
        // credentials; minting a session there would hand out more than the link grants.
        if (HttpContext.IsShareAccess())
            return null;

        var tenant = _tenantAccessor.Context;
        if (tenant is null || !tenant.IsDemo)
        {
            _logger.LogDebug("Demo session requested on a non-demo tenant");
            return null;
        }

        var subjectId = await _demoTenantService.FindDemoMemberSubjectIdAsync(tenant.TenantId, ct);
        if (subjectId is null)
        {
            _logger.LogWarning(
                "Demo tenant {TenantId} has no demo member — cannot issue a demo session", tenant.TenantId);
        }

        return subjectId;
    }
}

/// <summary>Token pair for headless clients that ask for <c>format=json</c>.</summary>
public record DemoSessionResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);
