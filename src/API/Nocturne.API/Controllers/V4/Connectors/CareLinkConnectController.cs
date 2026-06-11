using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Connectors.CareLink.Services;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Controllers.V4.Connectors;

/// <summary>
/// Drives the user-driven ("manual-paste") CareLink sign-in. The user opens the returned authorize URL,
/// signs in and solves the CAPTCHA in their own browser, then pastes the resulting code back. The server
/// exchanges it (PKCE) for a refresh token and stores it as the connector secret. This is the only viable
/// interactive flow: headless login is CAPTCHA-blocked, and Medtronic's Auth0 client only allows the
/// <c>com.medtronic.carepartner:/sso</c> custom-scheme redirect, so no web callback can receive the code.
/// </summary>
[ApiController]
[Route("api/v4/connectors/carelink/connect")]
[Authorize]
public partial class CareLinkConnectController : ControllerBase
{
    private const string ConnectorName = "CareLink";
    private static readonly TimeSpan FlowTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Scope carried by a desktop link token. Deliberately outside the OAuth scope vocabulary:
    /// MemberScopeMiddleware intersects member permissions with token scopes, so the resulting
    /// PermissionTrie is empty and every permission-gated endpoint denies the token — it only
    /// authenticates plain <c>[Authorize]</c> endpoints such as this controller's.
    /// </summary>
    private const string DesktopTokenScope = "connectors:carelink:connect";
    private static readonly TimeSpan DesktopTokenLifetime = TimeSpan.FromMinutes(10);

    private readonly IConnectorConfigurationService _configService;
    private readonly IMemoryCache _cache;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IJwtService _jwtService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CareLinkConnectController> _logger;

    public CareLinkConnectController(
        IConnectorConfigurationService configService,
        IMemoryCache cache,
        ITenantAccessor tenantAccessor,
        IJwtService jwtService,
        ILoggerFactory loggerFactory,
        ILogger<CareLinkConnectController> logger)
    {
        _configService = configService;
        _cache = cache;
        _tenantAccessor = tenantAccessor;
        _jwtService = jwtService;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Server-side flow state stashed between start and complete, keyed by tenant + state.</summary>
    private sealed record FlowState(
        string CodeVerifier, string ClientId, string TokenUrl, string RedirectUri, string? Audience, string Server);

    private string CacheKey(string state) => $"carelink-oauth:{_tenantAccessor.Context?.TenantId}:{state}";

    /// <summary>
    /// Begins the connect flow: builds the Auth0 authorize URL and stashes the PKCE verifier server-side.
    /// The client opens <c>AuthorizeUrl</c> in a new tab.
    /// </summary>
    [HttpPost("start")]
    [RemoteCommand]
    [ProducesResponseType(typeof(CareLinkConnectStartResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CareLinkConnectStartResponse>> Start(
        [FromBody] CareLinkConnectStartRequest request, CancellationToken ct)
    {
        var server = string.IsNullOrWhiteSpace(request.Server) ? "EU" : request.Server.Trim().ToUpperInvariant();
        if (server != "EU" && server != "US")
            return BadRequest(new { message = "Server must be 'EU' or 'US'." });

        using var flow = new CareLinkAuthFlowService(_loggerFactory.CreateLogger<CareLinkAuthFlowService>());
        var authorize = await flow.BuildAuthorizeUrlAsync(server, ct);
        if (authorize == null)
            return BadRequest(new { message = "Could not start CareLink sign-in. Please try again." });

        _cache.Set(
            CacheKey(authorize.State),
            new FlowState(authorize.CodeVerifier, authorize.ClientId, authorize.TokenUrl, authorize.RedirectUri, authorize.Audience, server),
            FlowTtl);

        return Ok(new CareLinkConnectStartResponse
        {
            AuthorizeUrl = authorize.AuthorizeUrl,
            State = authorize.State,
        });
    }

    /// <summary>
    /// Completes the connect flow: exchanges the pasted code for a refresh token and stores it as the
    /// connector secret. Returns the discovered username/country for auto-filling the config form.
    /// </summary>
    [HttpPost("complete")]
    [RemoteCommand(Invalidates = ["GetConfiguration", "GetAllConnectorStatus"])]
    [ProducesResponseType(typeof(CareLinkConnectCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CareLinkConnectCompleteResponse>> Complete(
        [FromBody] CareLinkConnectCompleteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
            return BadRequest(new { message = "Both code and state are required." });

        // Accept either the bare code or the full redirect URL the user copied.
        var code = ExtractCode(request.Code.Trim());

        if (!_cache.TryGetValue(CacheKey(request.State), out FlowState? flowState) || flowState == null)
            return BadRequest(new { message = "Sign-in session expired or not found. Start the connect flow again." });

        using var flow = new CareLinkAuthFlowService(_loggerFactory.CreateLogger<CareLinkAuthFlowService>());
        var result = await flow.ExchangeCodeAsync(
            code, flowState.CodeVerifier, flowState.ClientId, flowState.TokenUrl, flowState.RedirectUri, flowState.Audience, ct);
        if (result == null)
            return BadRequest(new { message = "Could not complete sign-in. The code may have expired — start the flow again." });

        _cache.Remove(CacheKey(request.State));

        // Persist the four secrets the refresh-token path needs (mirrors what carelink-bridge's
        // logindata.json carries). Username/server/country stay on the (non-secret) connector config.
        var secrets = new Dictionary<string, string>
        {
            ["refresh_token"] = result.RefreshToken,
            ["client_id"] = result.ClientId,
            ["token_url"] = result.TokenUrl,
        };
        if (!string.IsNullOrEmpty(result.Audience))
            secrets["audience"] = result.Audience;

        await _configService.SaveSecretsAsync(ConnectorName, secrets, User.Identity?.Name ?? "carelink-connect", ct);

        // Best-effort: auto-fill username/country from the profile so setup is one click.
        string? username = null, country = null;
        try
        {
            var me = await flow.FetchUserInfoAsync(result.AccessToken, flowState.Server, ct);
            username = me?.Username;
            country = me?.Country;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CareLink connect: profile auto-fill fetch failed (non-fatal)");
        }

        _logger.LogInformation("CareLink connect completed for tenant {Tenant}", _tenantAccessor.Context?.TenantId);

        return Ok(new CareLinkConnectCompleteResponse
        {
            Success = true,
            Username = username,
            Country = country,
        });
    }

    /// <summary>
    /// Mints a short-lived link code for the desktop companion app. The code carries the server URL
    /// plus a tenant-pinned bearer token scoped to this controller, so the app can drive
    /// <c>start</c>/<c>complete</c> for the user's tenant without a second login. The app intercepts
    /// the <c>com.medtronic.carepartner:/sso</c> redirect in its own webview, removing the
    /// manual code-paste step of the browser flow.
    /// </summary>
    [HttpPost("desktop-token")]
    [RemoteCommand]
    [ProducesResponseType(typeof(CareLinkDesktopTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CareLinkDesktopTokenResponse> DesktopToken()
    {
        var auth = HttpContext.GetAuthContext();
        var tenantId = _tenantAccessor.Context?.TenantId;
        if (auth is not { IsAuthenticated: true, SubjectId: not null } || tenantId is null)
            return Unauthorized();

        var subjectId = auth.SubjectId ?? throw new InvalidOperationException("Authenticated subject id is required.");

        var token = _jwtService.GenerateAccessToken(
            new SubjectInfo
            {
                Id = subjectId,
                Name = auth.SubjectName ?? string.Empty,
                Email = auth.Email,
            },
            permissions: [],
            roles: [],
            scopes: [DesktopTokenScope],
            tenantId: tenantId,
            lifetime: DesktopTokenLifetime);

        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value;
        var forwardedProto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var scheme = forwardedProto is "http" or "https" ? forwardedProto : Request.Scheme;
        var serverUrl = $"{scheme}://{host}";

        _logger.LogInformation(
            "CareLink desktop link code minted for tenant {Tenant} by subject {Subject}",
            tenantId, subjectId);

        return Ok(new CareLinkDesktopTokenResponse
        {
            LinkCode = $"nocturne-connect://link?server={Uri.EscapeDataString(serverUrl)}&token={Uri.EscapeDataString(token)}",
            ExpiresInSeconds = (int)DesktopTokenLifetime.TotalSeconds,
        });
    }

    private static string ExtractCode(string input)
    {
        var match = CodeRegex().Match(input);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : input;
    }

    [GeneratedRegex(@"[?&]code=([^&\s]+)")]
    private static partial Regex CodeRegex();
}

/// <summary>Request to begin the CareLink connect flow.</summary>
public class CareLinkConnectStartRequest
{
    /// <summary>Region: "EU" (Outside-US, incl. Australia) or "US".</summary>
    public string Server { get; set; } = "EU";
}

/// <summary>The authorize URL to open and the opaque state to echo back on completion.</summary>
public class CareLinkConnectStartResponse
{
    public string AuthorizeUrl { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

/// <summary>Request to complete the flow with the code captured from the redirect.</summary>
public class CareLinkConnectCompleteRequest
{
    /// <summary>The authorization code, or the full <c>com.medtronic.carepartner:/sso?code=...</c> URL.</summary>
    public string Code { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

/// <summary>Result of completing the flow, with optional profile details for auto-fill.</summary>
public class CareLinkConnectCompleteResponse
{
    public bool Success { get; set; }
    public string? Username { get; set; }
    public string? Country { get; set; }
}

/// <summary>A link code for the desktop companion app.</summary>
public class CareLinkDesktopTokenResponse
{
    /// <summary>
    /// <c>nocturne-connect://link?server=…&amp;token=…</c> — pasted into the desktop app (or later
    /// opened as a deep link). Carries the server base URL and a short-lived bearer token.
    /// </summary>
    public string LinkCode { get; set; } = string.Empty;

    /// <summary>Seconds until the embedded token expires.</summary>
    public int ExpiresInSeconds { get; set; }
}
