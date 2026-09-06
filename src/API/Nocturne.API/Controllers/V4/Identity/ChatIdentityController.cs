using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Extensions;

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
// Every endpoint here binds or reads a chat identity for a subject. The demo's subject is shared,
// so ownership does not separate one visitor from the next: a visitor's Discord or Telegram
// binding would be the next visitor's to read and revoke.
[DenyDemoSubject]
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

    /// <summary>The authenticated subject, or <c>null</c> for a credential that carries none.</summary>
    private Guid? SubjectId => (HttpContext.Items["AuthContext"] as AuthContext)?.SubjectId;

    /// <summary>
    /// Resolves the authenticated user's subject ID or throws if unavailable.
    /// </summary>
    /// <returns>The authenticated subject's <see cref="Guid"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="AuthContext"/> is missing or has no subject ID.</exception>
    private Guid GetUserIdOrThrow()
    {
        var authContext = HttpContext.GetAuthContext()
            ?? throw new InvalidOperationException("AuthContext not available");
        return authContext.SubjectId
            ?? throw new InvalidOperationException("Authenticated request has no subject id");
    }

    /// <summary>The rows a by-id request from this caller may reach.</summary>
    /// <seealso cref="ChatLinkScope"/>
    private ChatLinkScope OwnedByCaller()
        => ChatLinkScope.ForOwner(_tenantAccessor.TenantId, GetUserIdOrThrow());

    /// <summary>List active chat identity links for the current tenant.</summary>
    /// <remarks>
    /// <para>
    /// The list stays tenant-scoped, so a chat account linked to several tenants shows a
    /// non-default row here while its default sits on a row this tenant cannot see. Naming that
    /// row's label closes the gap without widening the list, and it is filled in only for the
    /// caller's own links: a co-member's chat account may be linked to tenants the caller has no
    /// part in, and its label is that tenant's slug.
    /// </para>
    /// <para>
    /// A co-member's row is listed so the tenant's bot routing is legible, but the chat account
    /// behind it is the co-member's and not this tenant's to hand out, so
    /// <see cref="ChatIdentityLinkResponse.PlatformUserId"/> is withheld on the same terms.
    /// </para>
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<ChatIdentityLinkResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ChatIdentityLinkResponse>>> GetLinks(CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var subjectId = SubjectId;
        var links = await _service.GetByTenantAsync(tenantId, ct);

        var responses = new List<ChatIdentityLinkResponse>(links.Count);
        foreach (var link in links)
        {
            var ownedByCaller = link.NocturneUserId == subjectId;
            var response = MapResponse(link, ownedByCaller);
            if (ownedByCaller)
            {
                var holder = await _service.GetDefaultAsync(link.Platform, link.PlatformUserId, ct);
                response.DefaultLabel = holder?.Label;
            }

            responses.Add(response);
        }

        return Ok(responses);
    }

    /// <summary>Claim a pending link token after /connect slash command auth.</summary>
    /// <remarks>
    /// A token carries no subject, and a chat account already linked to this tenant keeps the
    /// link it has, so the row this returns may belong to another subject and predate the claim.
    /// </remarks>
    [HttpPost("links/claim")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(typeof(ChatIdentityLinkResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatIdentityLinkResponse>> ClaimLink(
        [FromBody] ClaimChatIdentityLinkRequest body, CancellationToken ct)
    {
        var tenantId = _tenantAccessor.TenantId;
        var userId = GetUserIdOrThrow();
        var entry = await _service.ClaimPendingLinkAsync(tenantId, userId, body.Token, ct);
        return Ok(MapResponse(entry, ownedByCaller: entry.NocturneUserId == userId));
    }

    /// <inheritdoc cref="ChatIdentityService.SetDefaultAsync"/>
    [HttpPost("links/{id:guid}/set-default")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        try
        {
            await _service.SetDefaultAsync(OwnedByCaller(), id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        return NoContent();
    }

    [HttpPatch("links/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateLink(
        Guid id, [FromBody] UpdateChatIdentityLinkRequest body, CancellationToken ct)
    {
        var scope = OwnedByCaller();
        try
        {
            if (body.Label is not null)
                await _service.RenameLabelAsync(scope, id, body.Label, ct);
            if (body.DisplayName is not null)
                await _service.UpdateDisplayNameAsync(scope, id, body.DisplayName, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <inheritdoc cref="ChatIdentityService.RevokeAsync"/>
    [HttpDelete("links/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetLinks"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RevokeLink(Guid id, CancellationToken ct)
    {
        try
        {
            await _service.RevokeAsync(OwnedByCaller(), id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
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

    private static ChatIdentityLinkResponse MapResponse(
        ChatIdentityDirectoryEntry e, bool ownedByCaller)
        => new()
        {
            Id = e.Id,
            TenantId = e.TenantId,
            NocturneUserId = e.NocturneUserId,
            Platform = e.Platform,
            PlatformUserId = ownedByCaller ? e.PlatformUserId : null,
            IsOwnedByCaller = ownedByCaller,
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

    /// <summary>
    /// The chat account this link routes, or <c>null</c> on a link belonging to another subject.
    /// </summary>
    /// <seealso cref="ChatIdentityController.GetLinks"/>
    public string? PlatformUserId { get; set; }

    public string? PlatformChannelId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    /// <summary>
    /// Label of the link a bare bot invocation from this chat account resolves to, which may be a
    /// link in another tenant. Null when the chat account has no default, and on links belonging to
    /// another subject.
    /// </summary>
    public string? DefaultLabel { get; set; }

    /// <summary>
    /// Whether the caller's own subject owns this link, and so whether the by-id endpoints on
    /// <see cref="ChatIdentityController"/> will act on it rather than answering 404.
    /// </summary>
    public bool IsOwnedByCaller { get; set; }

    public string DisplayUnit { get; set; } = "mg/dL";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ClaimChatIdentityLinkRequest
{
    public string Token { get; set; } = string.Empty;
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
