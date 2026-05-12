using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Tenant-scoped chat identity link management. Backed by the global
/// ChatIdentityDirectory table via <see cref="ChatIdentityService"/>.
/// </summary>
/// <seealso cref="ChatIdentityService"/>
/// <seealso cref="ITenantAccessor"/>
[ApiController]
[Tags("Identity")]
[Authorize]
[Route("api/v4/chat-identity")]
public class ChatIdentityController : ControllerBase
{
    private readonly ChatIdentityService _service;
    private readonly ITenantAccessor _tenantAccessor;

    /// <summary>
    /// Initializes a new instance of <see cref="ChatIdentityController"/>.
    /// </summary>
    /// <param name="service">Service managing chat identity link storage and retrieval.</param>
    /// <param name="tenantAccessor">Accessor for the current request tenant context.</param>
    public ChatIdentityController(
        ChatIdentityService service,
        ITenantAccessor tenantAccessor)
    {
        _service = service;
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>
    /// Resolves the authenticated user's subject ID or throws if unavailable.
    /// </summary>
    /// <returns>The authenticated subject's <see cref="Guid"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="AuthContext"/> is missing or has no subject ID.</exception>
    private Guid GetUserIdOrThrow()
    {
        var authContext = HttpContext.Items["AuthContext"] as AuthContext
            ?? throw new InvalidOperationException("AuthContext not available");
        return authContext.SubjectId
            ?? throw new InvalidOperationException("Authenticated request has no subject id");
    }

    /// <summary>List active chat identity links for the current tenant.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<ChatIdentityLinkResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ChatIdentityLinkResponse>>> GetLinks(CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var links = await _service.GetByTenantAsync(tenantId, ct);
        return Ok(links.Select(MapResponse).ToList());
    }

    /// <summary>Claim a pending link token after /connect slash command auth.</summary>
    [HttpPost("links/claim")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(typeof(ChatIdentityLinkResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatIdentityLinkResponse>> ClaimLink(
        [FromBody] ClaimChatIdentityLinkRequest body, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var userId = GetUserIdOrThrow();
        var entry = await _service.ClaimPendingLinkAsync(tenantId, userId, body.Token, ct);
        return Ok(MapResponse(entry));
    }

    /// <summary>Directly create a link for the current tenant (used by OAuth2 finalize hop).</summary>
    [HttpPost("links/direct")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(typeof(ChatIdentityLinkResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatIdentityLinkResponse>> CreateDirectLink(
        [FromBody] CreateDirectLinkRequest body, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var userId = GetUserIdOrThrow();
        var entry = await _service.CreateDirectLinkAsync(
            tenantId, userId, body.Platform, body.PlatformUserId, ct);
        return Ok(MapResponse(entry));
    }

    /// <inheritdoc cref="ChatIdentityService.SetDefaultAsync"/>
    [HttpPost("links/{id:guid}/set-default")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        await _service.SetDefaultAsync(tenantId, id, ct);
        return NoContent();
    }

    [HttpPatch("links/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> UpdateLink(
        Guid id, [FromBody] UpdateChatIdentityLinkRequest body, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        if (body.Label is not null)
            await _service.RenameLabelAsync(tenantId, id, body.Label, ct);
        if (body.DisplayName is not null)
            await _service.UpdateDisplayNameAsync(tenantId, id, body.DisplayName, ct);
        return NoContent();
    }

    /// <inheritdoc cref="ChatIdentityService.RevokeAsync"/>
    [HttpDelete("links/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> RevokeLink(Guid id, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        await _service.RevokeAsync(tenantId, id, ct);
        return NoContent();
    }

    /// <summary>
    /// Read-only lookup of a pending link token, used by the authorize page to
    /// validate and render the confirmation step.
    /// </summary>
    [HttpGet("links/pending/{token}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PendingLinkViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PendingLinkViewResponse>> GetPending(
        string token, CancellationToken ct)
    {
        var pending = await _service.GetPendingAsync(token, ct);
        if (pending is null) return NotFound();

        // Slug-binding is verified by ClaimPendingLinkAsync when the user confirms.
        return Ok(new PendingLinkViewResponse
        {
            Platform = pending.Platform,
            PlatformUserId = pending.PlatformUserId,
            Source = pending.Source,
        });
    }

    private static ChatIdentityLinkResponse MapResponse(ChatIdentityDirectoryEntry e)
        => new()
        {
            Id = e.Id,
            TenantId = e.TenantId,
            NocturneUserId = e.NocturneUserId,
            Platform = e.Platform,
            PlatformUserId = e.PlatformUserId,
            PlatformChannelId = e.PlatformChannelId,
            Label = e.Label,
            DisplayName = e.DisplayName,
            IsDefault = e.IsDefault,
            DisplayUnit = e.DisplayUnit,
            IsActive = e.IsActive,
            CreatedAt = e.CreatedAt,
        };
}

#region DTOs

public class ChatIdentityLinkResponse
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid NocturneUserId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
    public string? PlatformChannelId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public string DisplayUnit { get; set; } = "mg/dL";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ClaimChatIdentityLinkRequest
{
    public string Token { get; set; } = string.Empty;
}

public class CreateDirectLinkRequest
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
}

public class UpdateChatIdentityLinkRequest
{
    public string? Label { get; set; }
    public string? DisplayName { get; set; }
}

public class PendingLinkViewResponse
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

#endregion
