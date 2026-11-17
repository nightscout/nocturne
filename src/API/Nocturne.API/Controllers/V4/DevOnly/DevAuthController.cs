using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Models.DevOnly;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.DevOnly;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Controllers.V4.DevOnly;

/// <summary>
/// Dev-only authentication helpers: a session login that bypasses WebAuthn (for
/// browsers and fully headless agents) and export of registered passkeys as a
/// committable dev identity fixture. Conditionally excluded from production
/// builds — the .DevOnly namespace is stripped from controller discovery
/// outside Development.
/// </summary>
[ApiController]
[Route("api/v4/dev-only/auth")]
[AllowAnonymous]
[AllowDuringSetup]
[Produces("application/json")]
public class DevAuthController : ControllerBase
{
    private readonly INocturneDbContextFactory _dbFactory;
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly IPasskeyCredentialRepository _passkeys;
    private readonly ISessionService _sessionService;
    private readonly IOptions<OidcOptions> _oidcOptions;
    private readonly IOptions<BaseDomainOptions> _baseDomainOptions;
    private readonly ILogger<DevAuthController> _logger;

    public DevAuthController(
        INocturneDbContextFactory dbFactory,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        IPasskeyCredentialRepository passkeys,
        ISessionService sessionService,
        IOptions<OidcOptions> oidcOptions,
        IOptions<BaseDomainOptions> baseDomainOptions,
        ILogger<DevAuthController> logger)
    {
        _dbFactory = dbFactory;
        _tenants = tenants;
        _members = members;
        _passkeys = passkeys;
        _sessionService = sessionService;
        _oidcOptions = oidcOptions;
        _baseDomainOptions = baseDomainOptions;
        _logger = logger;
    }

    /// <summary>
    /// Log in as a tenant member without a WebAuthn ceremony and set the normal
    /// session cookies. Navigating a browser to this URL on a tenant subdomain
    /// (e.g. https://dev.nocturne.localhost:1612/api/v4/dev-only/auth/login)
    /// leaves it authenticated and redirects into the app. The tenant resolves
    /// from the ?tenant slug, else from the request host; the member from
    /// ?username, else the first owner-role member, else the first member.
    /// Add format=json to get the token pair instead of a redirect.
    /// A state-changing GET is deliberate — browser navigation is the use case —
    /// which makes it CSRF-able while a dev stack runs; acceptable only because
    /// the controller does not exist outside Development.
    /// </summary>
    [HttpGet("login")]
    public async Task<IActionResult> Login(
        [FromQuery] string? tenant,
        [FromQuery] string? username,
        [FromQuery] string? redirect,
        [FromQuery] string? format,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(ct);
        var (error, response) = await IssueSessionAsync(db, tenant, username, ct);
        if (error != null)
            return error;

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return Ok(response);

        var target = redirect != null && Url.IsLocalUrl(redirect) ? redirect : "/";
        return Redirect(target);
    }

    /// <summary>
    /// JSON variant of <see cref="Login"/> for headless clients: returns the
    /// token pair (and also sets the session cookies on the response).
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<DevLoginResponse>> LoginJson(
        [FromBody] DevLoginRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(ct);
        var (error, response) = await IssueSessionAsync(db, request.Tenant, request.Username, ct);
        if (error != null)
            return error;
        return Ok(response);
    }

    /// <summary>
    /// Export registered passkeys as a dev identity fixture. Save the output to
    /// docs/seed/dev-identities.json (committable — WebAuthn public keys are not
    /// secret) and startup re-seeds it after every DB wipe, while seed-tenant
    /// adds each fixture subject as an owner of new tenants. Synthetic dev-seed
    /// credentials are excluded. Optional ?username= filters to one identity.
    /// </summary>
    [HttpGet("passkey-fixture")]
    public async Task<ActionResult<DevIdentityFixtureFile>> ExportPasskeyFixture(
        [FromQuery] string? username, CancellationToken ct)
    {
        var holders = await _passkeys.ListHoldersAsync(ct);

        var identities = holders
            .Select(holder => new DevIdentityDto
            {
                SubjectId = holder.Subject.Id,
                Name = holder.Subject.Name,
                Username = holder.Subject.Username,
                Email = holder.Subject.Email,
                Credentials = holder.Credentials
                    .Where(c => !DevIdentityFixtureSeeder.IsSyntheticCredentialId(c.CredentialId))
                    .Select(c => new DevIdentityCredentialDto
                    {
                        CredentialId = Convert.ToBase64String(c.CredentialId),
                        PublicKey = Convert.ToBase64String(c.PublicKey),
                        Transports = c.Transports.ToList(),
                        Label = c.Label,
                        AaGuid = c.AaGuid,
                    }).ToList(),
            })
            .Where(i => i.Credentials.Count > 0)
            .Where(i => username == null
                || string.Equals(i.Username, username, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Name, username, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Ok(new DevIdentityFixtureFile { Identities = identities });
    }

    private async Task<(ActionResult? Error, DevLoginResponse? Response)> IssueSessionAsync(
        NocturneDbContext db,
        string? tenantSlug, string? username, CancellationToken ct)
    {
        var slug = tenantSlug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = SubdomainParser.Extract(Request.Host.Host, _baseDomainOptions.Value.BaseDomain);
        }

        TenantRecord? tenant;
        if (!string.IsNullOrWhiteSpace(slug))
        {
            tenant = await _tenants.FindBySlugAsync(slug, ct);
            if (tenant == null)
                return (NotFound(new { error = $"No tenant with slug '{slug}'" }), null);
        }
        else
        {
            tenant = await _tenants.FindSoleTenantAsync(ct);
            if (tenant is null)
                return (BadRequest(new
                {
                    error = "Tenant could not be resolved from the host. Pass ?tenant=<slug>.",
                }), null);
        }

        // ?tenant= may name a tenant other than the host's; the repository pins the read to it.
        var members = await _members.ListMembersAsync(tenant.Id, ct);

        var candidates = DevTenantMemberSelection.Candidates(members);
        if (candidates.Count == 0)
            return (BadRequest(new { error = $"Tenant '{tenant.Slug}' has no members" }), null);

        TenantMemberRecord? member;
        if (!string.IsNullOrWhiteSpace(username))
        {
            member = candidates.FirstOrDefault(m =>
                string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.SubjectUsername, username, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.SubjectName, username, StringComparison.OrdinalIgnoreCase));
            if (member == null)
                return (NotFound(new
                {
                    error = $"No member '{username}' on tenant '{tenant.Slug}'",
                }), null);
        }
        else
        {
            member = DevTenantMemberSelection.PickOwnerOrFirst(candidates);
        }

        var session = await _sessionService.IssueSessionAsync(
            member.SubjectId,
            new SessionContext(
                DeviceDescription: "dev-login",
                IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent: Request.Headers.UserAgent.FirstOrDefault()),
            ct);

        Response.SetSessionCookies(session, _oidcOptions.Value);

        _logger.LogInformation(
            "Dev login: subject {SubjectId} on tenant {Slug}", member.SubjectId, tenant.Slug);

        return (null, new DevLoginResponse(
            tenant.Id,
            tenant.Slug,
            member.SubjectId,
            member.Username ?? member.SubjectUsername ?? member.SubjectName ?? string.Empty,
            session.AccessToken,
            session.RefreshToken,
            session.ExpiresInSeconds));
    }
}

public record DevLoginRequest(string? Tenant, string? Username);

public record DevLoginResponse(
    Guid TenantId,
    string Slug,
    Guid SubjectId,
    string? Username,
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds);
