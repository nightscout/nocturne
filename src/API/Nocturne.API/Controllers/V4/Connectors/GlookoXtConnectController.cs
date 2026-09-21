using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Xt;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Connectors;

/// <summary>
/// Drives the Glooko XT sign-in — the Glooko connector with <c>Server = XT</c> — which no
/// background job can complete on its own: the password
/// step makes Glooko XT email the patient a one-time code, and only that code yields the JWT the
/// connector runs on. The tenant types the code here, the server trades it for the token and
/// stores the token as the connector secret. The password is used once and never stored.
/// </summary>
[ApiController]
[Route("api/v4/connectors/glooko/connect/xt")]
[Authorize]
[RequireScope(Scope.TenantSettings)]
// Completing the flow writes a year-long token for a real patient account into the shared demo
// tenant, where every visitor is the same member; refused before the code is burned.
[DenyDemoSubject]
public class GlookoXtConnectController(
    IConnectorConfigurationService configService,
    IServiceProvider services,
    ITenantAccessor tenantAccessor,
    IConnectorAttentionNotifier attentionNotifier,
    ILogger<GlookoXtConnectController> logger) : ControllerBase
{
    private const string ConnectorName = "Glooko";

    /// <summary>
    /// Step one: checks the email and password with Glooko XT, which then emails the account a
    /// one-time code. Nothing is stored.
    /// </summary>
    [HttpPost("request-code")]
    [RemoteCommand]
    [ProducesResponseType(typeof(GlookoXtConnectStepResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<GlookoXtConnectStepResponse>> RequestCode(
        [FromBody] GlookoXtRequestCodeRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new { message = "Email and password are required." });

        if (LoginClient() is not { } login)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The Glooko XT connector is disabled on this server." });

        var step = await login.RequestCodeAsync(email, request.Password, ct);
        if (!step.Success)
            return BadRequest(new { message = step.Error ?? "Glooko XT did not accept the sign-in." });

        return Ok(new GlookoXtConnectStepResponse { Success = true });
    }

    /// <summary>
    /// Step two: trades the emailed code for the access token, stores it as the connector secret
    /// and records the email on the connector configuration so the sync has an account to name.
    /// </summary>
    [HttpPost("complete")]
    [RemoteCommand(Invalidates = ["GetConfiguration", "GetAllConnectorStatus"])]
    [ProducesResponseType(typeof(GlookoXtConnectCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<GlookoXtConnectCompleteResponse>> Complete(
        [FromBody] GlookoXtCompleteRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim();
        var code = request.Code?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return BadRequest(new { message = "Email and code are required." });

        if (LoginClient() is not { } login)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The Glooko XT connector is disabled on this server." });

        var step = await login.RedeemCodeAsync(email, code, ct);
        if (!step.Success || string.IsNullOrEmpty(step.Token))
            return BadRequest(new { message = step.Error ?? "Glooko XT did not accept the code." });

        // The secret key is the camelCase form of the configuration property the sync reads.
        await configService.SaveSecretsAsync(ConnectorName,
            new Dictionary<string, string> { ["accessToken"] = step.Token },
            User.Identity?.Name ?? "glookoxt-connect", ct);

        await PersistEmailAsync(email, ct);

        // A fresh token answers whatever the failing syncs asked of the owner.
        if (tenantAccessor.Context?.TenantId is { } tenantId)
            await attentionNotifier.ApplyAsync(tenantId, "glooko", "Glooko", null, ct);

        var expiresAt = GlookoXtJwt.TryGetExpiry(step.Token);
        logger.LogInformation("Glooko XT connect completed for tenant {Tenant}", tenantAccessor.Context?.TenantId);

        return Ok(new GlookoXtConnectCompleteResponse { Success = true, Email = email, Server = GlookoConstants.RegionXT, TokenExpiresAt = expiresAt });
    }

    /// <summary>
    /// Null when the connector is switched off at the host, which leaves its services unregistered;
    /// the flow then has nothing to sign in with and says so instead of failing to resolve.
    /// </summary>
    private GlookoXtLoginClient? LoginClient() => services.GetService<GlookoXtLoginClient>();

    /// <summary>
    /// Writes the signed-in email and the XT region into the connector configuration: the email is
    /// the connector's one required setting, and the region is what routes the sync to Glooko XT
    /// at all — the tokens just stored belong to it. Merged into the stored document because the
    /// save replaces the whole thing. Never fails the connect: the token is already stored and the
    /// form can be filled in by hand.
    /// </summary>
    private async Task PersistEmailAsync(string email, CancellationToken ct)
    {
        try
        {
            var existing = await configService.GetConfigurationAsync(ConnectorName, ct);
            using var merged = MergeEmail(existing?.Configuration, email);
            await configService.SaveConfigurationAsync(ConnectorName, merged, User.Identity?.Name ?? "glookoxt-connect", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Glooko XT connect: could not persist the signed-in email to the configuration");
        }
    }

    public static JsonDocument MergeEmail(JsonDocument? existing, string email)
    {
        var config = existing is not null
            ? JsonNode.Parse(existing.RootElement.GetRawText())?.AsObject() ?? new JsonObject()
            : new JsonObject();

        config["email"] = email;
        config["server"] = GlookoConstants.RegionXT;
        return JsonDocument.Parse(config.ToJsonString());
    }
}

/// <summary>Step one of the Glooko XT sign-in: the account's email and password.</summary>
public class GlookoXtRequestCodeRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>Step two: the same email and the code Glooko XT emailed.</summary>
public class GlookoXtCompleteRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class GlookoXtConnectStepResponse
{
    public bool Success { get; set; }
}

public class GlookoXtConnectCompleteResponse
{
    public bool Success { get; set; }
    public string? Email { get; set; }
    /// <summary>The Glooko region the stored token belongs to: always <c>XT</c>.</summary>
    public string? Server { get; set; }
    /// <summary>When the stored token lapses and the sign-in has to be repeated, if the token says.</summary>
    public DateTime? TokenExpiresAt { get; set; }
}
