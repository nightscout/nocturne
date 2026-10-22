using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Platform-admin controller for managing OIDC identity providers configured on the instance.
/// </summary>
/// <remarks>
/// Allows platform administrators to register, update, and remove OIDC providers (Google,
/// Microsoft, custom IdPs) that are presented on the login page. Only users with the
/// <c>platform_admin</c> role may access these endpoints. Endpoints are allowed during
/// initial setup (<see cref="AllowDuringSetupAttribute"/>) so that an admin can configure
/// a provider before any passkeys exist.
/// </remarks>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/admin/oidc-providers")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class OidcProviderAdminController : ControllerBase
{
    private readonly IOidcProviderService _providerService;
    private readonly INocturneDbContextFactory _contexts;
    private readonly IHttpClientFactory _httpClientFactory;

    public OidcProviderAdminController(
        IOidcProviderService providerService,
        INocturneDbContextFactory contexts,
        IHttpClientFactory httpClientFactory)
    {
        _providerService = providerService;
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<OidcProviderResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var providers = await _providerService.GetAllProvidersAsync();
        return Ok(providers.Select(OidcProviderResponse.FromDomain).ToList());
    }

    [HttpPost]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(typeof(OidcProviderResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateOidcProviderRequest request)
    {
        var provider = new OidcProvider
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            ProviderType = request.ProviderType,
            OAuth2 = request.OAuth2,
            IssuerUrl = request.IssuerUrl,
            ClientId = request.ClientId,
            ClientSecret = request.ClientSecret,
            // Null scopes are filled with the protocol's defaults by the provider service.
            Scopes = request.Scopes ?? [],
            ClaimMappings = request.ClaimMappings ?? new(),
            DefaultRoles = request.DefaultRoles ?? ["readable"],
            IsEnabled = request.IsEnabled,
            DisplayOrder = request.DisplayOrder,
            Icon = request.Icon,
            ButtonColor = request.ButtonColor
        };

        var created = await _providerService.CreateProviderAsync(provider);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, OidcProviderResponse.FromDomain(created));
    }

    /// <inheritdoc cref="IOidcProviderService.IsConfigManaged"/>
    [HttpGet("config-managed")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConfigManagedResponse), StatusCodes.Status200OK)]
    public IActionResult GetConfigManaged()
        => Ok(new ConfigManagedResponse(_providerService.IsConfigManaged));

    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(OidcProviderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var provider = await _providerService.GetProviderByIdAsync(id);
        return provider is null ? NotFound() : Ok(OidcProviderResponse.FromDomain(provider));
    }

    [HttpPut("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetAll", "GetById"])]
    [ProducesResponseType(typeof(OidcProviderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpdateOidcProviderRequest request, [FromQuery] bool force = false)
    {
        var existing = await _providerService.GetProviderByIdAsync(id);
        if (existing is null) return NotFound();

        if (existing.IsEnabled && !request.IsEnabled && !force)
        {
            await using var db = await _contexts.CreateAsync();
            var conflict = await CheckWouldLockOutUsersAsync(db, id, disable: true);
            if (conflict is not null) return conflict;
        }

        existing.Name = request.Name;
        existing.ProviderType = request.ProviderType;
        existing.OAuth2 = request.OAuth2;
        existing.IssuerUrl = request.IssuerUrl;
        existing.ClientId = request.ClientId;
        if (request.ClientSecret is not null)
            existing.ClientSecret = request.ClientSecret;
        existing.Scopes = request.Scopes ?? existing.Scopes;
        existing.ClaimMappings = request.ClaimMappings ?? existing.ClaimMappings;
        existing.DefaultRoles = request.DefaultRoles ?? existing.DefaultRoles;
        existing.IsEnabled = request.IsEnabled;
        existing.DisplayOrder = request.DisplayOrder;
        existing.Icon = request.Icon;
        existing.ButtonColor = request.ButtonColor;

        var updated = await _providerService.UpdateProviderAsync(existing);
        return updated is null ? NotFound() : Ok(OidcProviderResponse.FromDomain(updated));
    }

    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetAll"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force = false)
    {
        if (!force)
        {
            await using var db = await _contexts.CreateAsync();
            var conflict = await CheckWouldLockOutUsersAsync(db, id, disable: false);
            if (conflict is not null) return conflict;
        }

        var deleted = await _providerService.DeleteProviderAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    /// <inheritdoc cref="IOidcProviderService.EnableProviderAsync"/>
    [HttpPost("{id:guid}/enable")]
    [RemoteCommand(Invalidates = ["GetAll", "GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id)
    {
        var enabled = await _providerService.EnableProviderAsync(id);
        return enabled ? NoContent() : NotFound();
    }

    /// <inheritdoc cref="IOidcProviderService.DisableProviderAsync"/>
    [HttpPost("{id:guid}/disable")]
    [RemoteCommand(Invalidates = ["GetAll", "GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable(Guid id, [FromQuery] bool force = false)
    {
        if (!force)
        {
            await using var db = await _contexts.CreateAsync();
            var conflict = await CheckWouldLockOutUsersAsync(db, id, disable: true);
            if (conflict is not null) return conflict;
        }

        var disabled = await _providerService.DisableProviderAsync(id);
        return disabled ? NoContent() : NotFound();
    }

    /// <inheritdoc cref="IOidcProviderService.TestProviderAsync"/>
    [HttpPost("{id:guid}/test")]
    [RemoteCommand]
    [ProducesResponseType(typeof(OidcProviderTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestExisting(Guid id)
    {
        var provider = await _providerService.GetProviderByIdAsync(id);
        if (provider is null) return NotFound();

        var result = await _providerService.TestProviderAsync(id);
        return Ok(result);
    }

    [HttpPost("test")]
    [RemoteCommand]
    [ProducesResponseType(typeof(OidcProviderTestResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestUnsaved([FromBody] TestProviderRequest request)
    {
        var sw = Stopwatch.StartNew();
        var result = new OidcProviderTestResult();

        try
        {
            var discoveryUrl = request.IssuerUrl.TrimEnd('/') + "/.well-known/openid-configuration";

            // Named, not the unnamed default, which carries no address policy.
            var httpClient = _httpClientFactory.CreateClient("OidcProvider");
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var response = await httpClient.GetAsync(discoveryUrl);
            sw.Stop();
            result.ResponseTime = sw.Elapsed;

            if (!response.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Error = $"Discovery document returned HTTP {(int)response.StatusCode}";
                return Ok(result);
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            result.Success = true;
            result.DiscoveryDocument = new OidcDiscoveryDocument
            {
                Issuer = doc.TryGetProperty("issuer", out var issuer) ? issuer.GetString() ?? "" : "",
                AuthorizationEndpoint = doc.TryGetProperty("authorization_endpoint", out var authEp) ? authEp.GetString() ?? "" : "",
                TokenEndpoint = doc.TryGetProperty("token_endpoint", out var tokenEp) ? tokenEp.GetString() ?? "" : "",
                UserInfoEndpoint = doc.TryGetProperty("userinfo_endpoint", out var userInfoEp) ? userInfoEp.GetString() : null,
                EndSessionEndpoint = doc.TryGetProperty("end_session_endpoint", out var endSessionEp) ? endSessionEp.GetString() : null,
                JwksUri = doc.TryGetProperty("jwks_uri", out var jwks) ? jwks.GetString() ?? "" : ""
            };

            if (string.IsNullOrEmpty(result.DiscoveryDocument.AuthorizationEndpoint))
                result.Warnings.Add("Discovery document is missing authorization_endpoint");
            if (string.IsNullOrEmpty(result.DiscoveryDocument.TokenEndpoint))
                result.Warnings.Add("Discovery document is missing token_endpoint");
            if (string.IsNullOrEmpty(result.DiscoveryDocument.JwksUri))
                result.Warnings.Add("Discovery document is missing jwks_uri");
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            result.ResponseTime = sw.Elapsed;
            result.Success = false;
            result.Error = "Connection timed out after 10 seconds";
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            result.ResponseTime = sw.Elapsed;
            result.Success = false;
            result.Error = $"Failed to connect: {ex.Message}";
        }

        return Ok(result);
    }

    /// <summary>
    /// Refuses with 409 when deleting or disabling the provider would leave any account with no
    /// sign-in method (<see cref="OrphanedSubjectFilter"/>): one whose only passkey-free way in is
    /// this provider. Returns <see langword="null"/> when the operation strands nobody.
    /// </summary>
    /// <remarks>
    /// The caller can pass <c>force</c> to go ahead anyway, e.g. to switch off a compromised
    /// provider. The stranded members then recover through a recovery code
    /// (<see cref="Identity.MemberRecoveryController"/>).
    /// </remarks>
    private async Task<IActionResult?> CheckWouldLockOutUsersAsync(NocturneDbContext db, Guid providerId, bool disable)
    {
        var strandedCount = await db.WorkingOidcIdentities()
            .Where(i => i.ProviderId == providerId)
            .Select(i => i.Subject!)
            .Where(s => s.IsActive
                && !s.IsSystemSubject
                && !s.IsDemoSubject
                && !db.PasskeyCredentials.Any(p => p.SubjectId == s.Id)
                && !db.WorkingOidcIdentities().Any(o => o.SubjectId == s.Id && o.ProviderId != providerId))
            .Select(s => s.Id)
            .Distinct()
            .CountAsync();

        if (strandedCount == 0)
            return null;

        var action = disable ? "Disabling" : "Deleting";
        return Conflict(new
        {
            Error = $"{action} this provider would leave accounts with no way to sign in",
            AffectedUserCount = strandedCount,
            Message = $"{strandedCount} user(s) sign in only through this provider and would be locked out"
        });
    }
}

// --- DTOs ---

public record CreateOidcProviderRequest(
    string Name,
    string IssuerUrl,
    string ClientId,
    string? ClientSecret = null,
    List<string>? Scopes = null,
    Dictionary<string, string>? ClaimMappings = null,
    List<string>? DefaultRoles = null,
    bool IsEnabled = true,
    int DisplayOrder = 0,
    string? Icon = null,
    string? ButtonColor = null,
    OidcProviderType ProviderType = OidcProviderType.Oidc,
    OAuth2ProviderSettings? OAuth2 = null);

public record UpdateOidcProviderRequest(
    string Name,
    string IssuerUrl,
    string ClientId,
    string? ClientSecret = null,
    List<string>? Scopes = null,
    Dictionary<string, string>? ClaimMappings = null,
    List<string>? DefaultRoles = null,
    bool IsEnabled = true,
    int DisplayOrder = 0,
    string? Icon = null,
    string? ButtonColor = null,
    OidcProviderType ProviderType = OidcProviderType.Oidc,
    OAuth2ProviderSettings? OAuth2 = null);

public record OidcProviderResponse(
    Guid Id,
    string Name,
    string IssuerUrl,
    string ClientId,
    bool HasSecret,
    List<string> Scopes,
    Dictionary<string, string> ClaimMappings,
    List<string> DefaultRoles,
    bool IsEnabled,
    int DisplayOrder,
    string? Icon,
    string? ButtonColor,
    OidcProviderType ProviderType,
    OAuth2ProviderSettings? OAuth2)
{
    public static OidcProviderResponse FromDomain(OidcProvider p) => new(
        p.Id,
        p.Name,
        p.IssuerUrl,
        p.ClientId,
        HasSecret: !string.IsNullOrEmpty(p.ClientSecret),
        p.Scopes,
        p.ClaimMappings,
        p.DefaultRoles,
        p.IsEnabled,
        p.DisplayOrder,
        p.Icon,
        p.ButtonColor,
        p.ProviderType,
        p.OAuth2);
}

public record ConfigManagedResponse(bool IsConfigManaged);

public record TestProviderRequest(
    string IssuerUrl,
    string? ClientId = null,
    string? ClientSecret = null);
