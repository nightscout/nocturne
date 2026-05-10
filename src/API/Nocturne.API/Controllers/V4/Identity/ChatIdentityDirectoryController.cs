using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Chat;
using Nocturne.Infrastructure.Data;

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

    /// <summary>
    /// Initializes a new instance of <see cref="ChatIdentityDirectoryController"/>.
    /// </summary>
    /// <param name="directory">Service for cross-tenant directory candidate lookups.</param>
    /// <param name="pending">Service for pending link token generation and resolution.</param>
    /// <param name="contextFactory">Factory for creating database context instances.</param>
    public ChatIdentityDirectoryController(
        ChatIdentityDirectoryService directory,
        ChatIdentityPendingLinkService pending,
        IDbContextFactory<NocturneDbContext> contextFactory)
    {
        _directory = directory;
        _pending = pending;
        _contextFactory = contextFactory;
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
        var row = await _directory.GetByIdAsync(id, ct);
        if (row is null) return NotFound();
        if (row.Platform != body.Platform || row.PlatformUserId != body.PlatformUserId)
            return Forbid();
        await _directory.RevokeAsync(id, ct);
        return NoContent();
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

public class RevokeByPlatformUserRequest
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
}
