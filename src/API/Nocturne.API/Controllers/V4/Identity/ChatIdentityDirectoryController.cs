using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Cross-tenant directory endpoints for chat platform identity routing.
/// Called server-to-server by the Discord bot from the apex host (no subdomain).
/// Instance-key authenticated only.
/// </summary>
/// <seealso cref="ChatIdentityDirectoryService"/>
/// <seealso cref="ChatIdentityPendingLinkService"/>
[ApiController]
[Tags("Identity")]
[Route("api/v4/chat-identity/directory")]
[RequireInstanceKeyAuth]
public class ChatIdentityDirectoryController : ControllerBase
{
    private readonly ChatIdentityDirectoryService _directory;
    private readonly ChatIdentityPendingLinkService _pending;
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly ITenantDbContextFactory _tenantContextFactory;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAlertAcknowledgementService _acknowledgementService;

    /// <summary>
    /// Initializes a new instance of <see cref="ChatIdentityDirectoryController"/>.
    /// </summary>
    /// <param name="directory">Service for cross-tenant directory candidate lookups.</param>
    /// <param name="pending">Service for pending link token generation and resolution.</param>
    /// <param name="contextFactory">Factory for creating database context instances.</param>
    /// <param name="tenantContextFactory">Factory for contexts scoped to the request's tenant.</param>
    /// <param name="tenantAccessor">Accessor for the request's resolved tenant.</param>
    /// <param name="acknowledgementService">The one alert acknowledgement decision.</param>
    public ChatIdentityDirectoryController(
        ChatIdentityDirectoryService directory,
        ChatIdentityPendingLinkService pending,
        IDbContextFactory<NocturneDbContext> contextFactory,
        ITenantDbContextFactory tenantContextFactory,
        ITenantAccessor tenantAccessor,
        IAlertAcknowledgementService acknowledgementService)
    {
        _directory = directory;
        _pending = pending;
        _contextFactory = contextFactory;
        _tenantContextFactory = tenantContextFactory;
        _tenantAccessor = tenantAccessor;
        _acknowledgementService = acknowledgementService;
    }

    /// <summary>
    /// Returns ALL directory candidates for a (platform, platformUserId).
    /// Caller is responsible for label disambiguation.
    /// Each candidate includes the tenantSlug (joined from tenants table).
    /// </summary>
    /// <param name="platform">Chat platform identifier (e.g., "discord", "telegram").</param>
    /// <param name="platformUserId">Unique user identifier on the specified platform.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="DirectoryCandidatesResponse"/> with all matching tenant candidates,
    /// or 404 if no candidates are found.
    /// </returns>
    [HttpGet("resolve")]
    public async Task<ActionResult<DirectoryCandidatesResponse>> Resolve(
        [FromQuery] string platform,
        [FromQuery] string platformUserId,
        CancellationToken ct)
    {
        var rows = await _directory.GetCandidatesAsync(platform, platformUserId, ct);
        if (rows.Count == 0) return NotFound();

        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var tenantIds = rows.Select(r => r.TenantId).ToList();
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Slug, t.DisplayName })
            .ToListAsync(ct);
        var slugByTenant = tenants.ToDictionary(t => t.Id, t => t.Slug);

        return Ok(new DirectoryCandidatesResponse
        {
            Candidates = rows.Select(r => new DirectoryCandidate
            {
                Id = r.Id,
                TenantId = r.TenantId,
                TenantSlug = slugByTenant.GetValueOrDefault(r.TenantId, ""),
                NocturneUserId = r.NocturneUserId,
                Label = r.Label,
                DisplayName = r.DisplayName,
                IsDefault = r.IsDefault,
            }).ToList(),
        });
    }

    /// <inheritdoc cref="ChatIdentityPendingLinkService.CreateAsync"/>
    [HttpPost("pending-links")]
    public async Task<ActionResult<PendingLinkResponse>> CreatePending(
        [FromBody] CreatePendingLinkRequest request, CancellationToken ct)
    {
        var token = await _pending.CreateAsync(
            request.Platform, request.PlatformUserId, request.TenantSlug, request.Source, ct);
        return Ok(new PendingLinkResponse { Token = token });
    }

    /// <summary>
    /// Revoke a link by id, verifying the (platform, platformUserId) on the row matches
    /// the body. Used by /disconnect from the bot.
    /// </summary>
    [HttpDelete("links/{id:guid}")]
    public async Task<ActionResult> RevokeByPlatformUser(
        Guid id,
        [FromBody] RevokeByPlatformUserRequest body,
        CancellationToken ct)
    {
        // Cross-tenant by design: the bot authenticates with the instance key from the apex host
        // and identifies the row by the chat account on it, not by a tenant.
        var row = await _directory.GetByIdAsync(id, ChatLinkScope.Unscoped, ct);
        if (row is null) return NotFound();
        if (row.Platform != body.Platform || row.PlatformUserId != body.PlatformUserId)
            return Forbid();
        await _directory.RevokeAsync(id, ChatLinkScope.Unscoped, ct);
        return NoContent();
    }

    /// <summary>
    /// Acknowledges an alert from chat as the member the link belongs to, not as the bot. The
    /// instance key holds <c>*</c>, so handing it to
    /// <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/> would let every linked
    /// chat user acknowledge for everyone. The authority carries the linked subject and no scopes
    /// of its own, so the decision rests on that member's membership: with <c>alerts.readwrite</c>
    /// the excursion is acknowledged for everyone, otherwise it is muted for that member alone.
    /// </summary>
    /// <remarks>
    /// Must be called on the link's tenant host, because the acknowledgement's realtime broadcasts
    /// go to the request's tenant. Without an <see cref="ChatAcknowledgeRequest.ExcursionId"/> the
    /// same decision is applied to every active excursion of the tenant, and the outcome reads
    /// <c>muted</c> if any was muted, else <c>acknowledged</c> if any was, else <c>closed</c>.
    /// <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/> reports an excursion
    /// someone else already acknowledged as <c>acknowledged</c> whoever asks, so the response names
    /// who is recorded on it instead of crediting the chat user.
    /// </remarks>
    [HttpPost("links/{id:guid}/acknowledge")]
    [ProducesResponseType(typeof(ChatAcknowledgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatAcknowledgeResponse>> AcknowledgeAsLinkedMember(
        Guid id,
        [FromBody] ChatAcknowledgeRequest body,
        CancellationToken ct)
    {
        var row = await _directory.GetByIdAsync(id, ChatLinkScope.Unscoped, ct);
        if (row is null || !row.IsActive) return NotFound();
        if (row.Platform != body.Platform || row.PlatformUserId != body.PlatformUserId)
            return Forbid();
        if (!_tenantAccessor.IsResolved || _tenantAccessor.TenantId != row.TenantId)
            return NotFound();

        await using var db = await _tenantContextFactory.CreateAsync(ct);
        List<Guid> excursionIds;
        if (body.ExcursionId is { } requested)
        {
            if (!await db.AlertExcursions.AsNoTracking().AnyAsync(e => e.Id == requested, ct))
                return NotFound();
            excursionIds = [requested];
        }
        else
        {
            var now = DateTime.UtcNow;
            excursionIds = await db.AlertExcursions.AsNoTracking()
                .Where(e => e.EndedAt == null || e.EndedAt > now)
                .Select(e => e.Id)
                .ToListAsync(ct);
        }

        var authority = new AlertAcknowledgementAuthority(
            row.NocturneUserId, new HashSet<string>(StringComparer.Ordinal));
        var acknowledgedBy = string.IsNullOrWhiteSpace(body.AcknowledgedBy)
            ? row.NocturneUserId.ToString()
            : body.AcknowledgedBy;

        var startedAt = DateTime.UtcNow;
        var outcomes = new Dictionary<Guid, AlertAcknowledgementOutcome>(excursionIds.Count);
        foreach (var excursionId in excursionIds)
        {
            outcomes[excursionId] = await _acknowledgementService.AcknowledgeExcursionAsync(
                row.TenantId, excursionId, acknowledgedBy, authority, broadcast: true, ct);
        }

        if (outcomes.ContainsValue(AlertAcknowledgementOutcome.Muted))
            return Ok(new ChatAcknowledgeResponse { Outcome = AlertAcknowledgementOutcome.Muted });

        var acknowledgedIds = outcomes
            .Where(o => o.Value == AlertAcknowledgementOutcome.Acknowledged)
            .Select(o => o.Key)
            .ToList();
        if (acknowledgedIds.Count == 0)
            return Ok(new ChatAcknowledgeResponse { Outcome = AlertAcknowledgementOutcome.Closed });

        var recorded = await db.AlertExcursions.AsNoTracking()
            .Where(e => acknowledgedIds.Contains(e.Id))
            .Select(e => new { e.AcknowledgedAt, e.AcknowledgedBy })
            .ToListAsync(ct);
        var earlier = recorded.Where(e => e.AcknowledgedAt < startedAt).ToList();
        if (earlier.Count < acknowledgedIds.Count)
        {
            return Ok(new ChatAcknowledgeResponse
            {
                Outcome = AlertAcknowledgementOutcome.Acknowledged,
                AcknowledgedBy = acknowledgedBy,
            });
        }

        return Ok(new ChatAcknowledgeResponse
        {
            Outcome = AlertAcknowledgementOutcome.Acknowledged,
            AlreadyAcknowledged = true,
            AcknowledgedBy = earlier.MaxBy(e => e.AcknowledgedAt)!.AcknowledgedBy,
        });
    }
}

public class DirectoryCandidatesResponse
{
    public List<DirectoryCandidate> Candidates { get; set; } = new();
}

public class DirectoryCandidate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string TenantSlug { get; set; } = string.Empty;
    public Guid NocturneUserId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public class CreatePendingLinkRequest
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
    public string? TenantSlug { get; set; }
    public string Source { get; set; } = "connect-slash";
}

public class PendingLinkResponse
{
    public string Token { get; set; } = string.Empty;
}

public class ChatAcknowledgeRequest
{
    /// <summary>The chat platform on the link, checked against the row.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The chat account on the link, checked against the row.</summary>
    public string PlatformUserId { get; set; } = string.Empty;

    /// <summary>The excursion to acknowledge, or null for every active excursion of the tenant.</summary>
    public Guid? ExcursionId { get; set; }

    /// <summary>The chat user's display name, recorded as who acknowledged.</summary>
    public string? AcknowledgedBy { get; set; }
}

public class ChatAcknowledgeResponse
{
    /// <inheritdoc cref="AcknowledgeExcursionResponse.Outcome"/>
    public AlertAcknowledgementOutcome Outcome { get; set; }

    /// <summary>
    /// With an <c>acknowledged</c> outcome, who is recorded as having acknowledged: the chat user
    /// when this request did it, else whoever had already.
    /// </summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// True when every excursion reported <c>acknowledged</c> had been acknowledged before this
    /// request, so <see cref="AcknowledgedBy"/> is someone else's acknowledgement.
    /// </summary>
    public bool AlreadyAcknowledged { get; set; }
}

public class RevokeByPlatformUserRequest
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
}
