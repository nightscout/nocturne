using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.V4.Admin;

[ApiController]
[Route("api/v4/admin/oidc-providers")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class OidcProviderAdminController : ControllerBase
{
    private readonly IOidcProviderService _providerService;
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;

    public OidcProviderAdminController(
        IOidcProviderService providerService,
        NocturneDbContext dbContext,
        IHttpClientFactory httpClientFactory)
    {
        _providerService = providerService;
        _dbContext = dbContext;
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
            IssuerUrl = request.IssuerUrl,
            ClientId = request.ClientId,
            ClientSecret = request.ClientSecret,
            Scopes = request.Scopes ?? ["openid", "profile", "email"],
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
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateOidcProviderRequest request)
    {
        var existing = await _providerService.GetProviderByIdAsync(id);
        if (existing is null) return NotFound();

        existing.Name = request.Name;
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
    public async Task<IActionResult> Delete(Guid id)
    {
        var conflict = await CheckWouldLockOutUsersAsync(id, disable: false);
        if (conflict is not null) return conflict;

        var deleted = await _providerService.DeleteProviderAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/enable")]
    [RemoteCommand(Invalidates = ["GetAll", "GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id)
    {
        var enabled = await _providerService.EnableProviderAsync(id);
        return enabled ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/disable")]
    [RemoteCommand(Invalidates = ["GetAll", "GetById"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable(Guid id)
    {
        var conflict = await CheckWouldLockOutUsersAsync(id, disable: true);
        if (conflict is not null) return conflict;

        var disabled = await _providerService.DisableProviderAsync(id);
        return disabled ? NoContent() : NotFound();
    }

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
            var httpClient = _httpClientFactory.CreateClient();
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
    /// Checks whether deleting or disabling the given provider would leave zero enabled providers
    /// while OIDC-only users exist. Returns a 409 Conflict result if so, or null if the operation is safe.
    /// </summary>
    private async Task<IActionResult?> CheckWouldLockOutUsersAsync(Guid providerId, bool disable)
    {
        var allProviders = await _providerService.GetAllProvidersAsync();

        // Count how many enabled providers would remain after this operation
        int remainingEnabled = disable
            ? allProviders.Count(p => p.IsEnabled && p.Id != providerId)
            : allProviders.Count(p => p.IsEnabled && p.Id != providerId);

        if (remainingEnabled > 0)
            return null;

        // Zero enabled providers would remain. Check for OIDC-only users.
        var oidcOnlyUserCount = await _dbContext.Subjects
            .Where(s => s.OidcIdentities.Any())
            .Where(s => !_dbContext.PasskeyCredentials.Any(p => p.SubjectId == s.Id))
            .Where(s => !_dbContext.TotpCredentials.Any(t => t.SubjectId == s.Id))
            .CountAsync();

        if (oidcOnlyUserCount == 0)
            return null;

        var action = disable ? "Disabling" : "Deleting";
        return Conflict(new
        {
            Error = $"{action} this provider would leave no enabled OIDC providers",
            AffectedUserCount = oidcOnlyUserCount,
            Message = $"{oidcOnlyUserCount} user(s) authenticate exclusively via OIDC and would be locked out"
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
    string? ButtonColor = null);

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
    string? ButtonColor = null);

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
    string? ButtonColor)
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
        p.ButtonColor);
}

public record ConfigManagedResponse(bool IsConfigManaged);

public record TestProviderRequest(
    string IssuerUrl,
    string? ClientId = null,
    string? ClientSecret = null);
