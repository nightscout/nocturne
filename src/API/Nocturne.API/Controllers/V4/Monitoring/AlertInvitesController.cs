using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for managing alert invite links (create, validate, redeem, revoke).
/// </summary>
/// <remarks>
/// An invite attaches a follower to an alert rule channel, so creating, redeeming and revoking one
/// all change who an alert reaches and require <see cref="Scope.AlertsReadWrite"/>. The
/// per-action <c>[Authorize]</c> alone is satisfied by read-only credentials such as a guest-link
/// session, which holds <c>alerts.read</c>. Validation stays <c>[AllowAnonymous]</c> — the
/// redemption flow reads it before the invitee has signed in.
/// </remarks>
/// <seealso cref="NocturneDbContext"/>
[ApiController]
[Tags("Monitoring")]
[Route("api/v4/alert-invites")]
public class AlertInvitesController : ControllerBase
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<AlertInvitesController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertInvitesController"/>.
    /// </summary>
    /// <param name="contextFactory">Tenant-scoped factory for creating <see cref="NocturneDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    public AlertInvitesController(
        ITenantDbContextFactory contextFactory,
        ILogger<AlertInvitesController> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generate an invite link for a follower to attach to a rule channel.
    /// </summary>
    [HttpPost]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(typeof(AlertInviteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertInviteResponse>> CreateInvite(
        [FromBody] CreateAlertInviteRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        // Verify the rule channel exists within this tenant
        var stepExists = await db.AlertRuleChannels
            .AnyAsync(s => s.Id == request.AlertRuleChannelId, ct);

        if (!stepExists)
            return Problem(detail: "Channel not found", statusCode: 400, title: "Bad Request");

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
            return Unauthorized();

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        var invite = new AlertInviteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            CreatedBy = subjectId.Value,
            Token = token,
            AlertRuleChannelId = request.AlertRuleChannelId,
            PermissionScope = request.PermissionScope ?? "view_acknowledge",
            IsUsed = false,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
        };

        db.AlertInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(ValidateInvite), new { token = invite.Token }, new AlertInviteResponse
        {
            Id = invite.Id,
            Token = invite.Token,
            AlertRuleChannelId = invite.AlertRuleChannelId,
            PermissionScope = invite.PermissionScope,
            IsUsed = invite.IsUsed,
            ExpiresAt = invite.ExpiresAt,
            CreatedAt = invite.CreatedAt,
        });
    }

    /// <summary>
    /// Validate an invite token (public endpoint for redemption flow).
    /// </summary>
    [HttpGet("{token}")]
    [AllowAnonymous]
    [EnableRateLimiting("invite-lookup")]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertInviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<AlertInviteResponse>> ValidateInvite(string token, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var invite = await db.AlertInvites
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Token == token, ct);

        if (invite is null)
            return NotFound();

        if (invite.IsUsed)
            return Problem(detail: "Invite has already been redeemed", statusCode: 410, title: "Gone");

        if (invite.ExpiresAt < DateTime.UtcNow)
            return Problem(detail: "Invite has expired", statusCode: 410, title: "Gone");

        return Ok(new AlertInviteResponse
        {
            Id = invite.Id,
            Token = invite.Token,
            AlertRuleChannelId = invite.AlertRuleChannelId,
            PermissionScope = invite.PermissionScope,
            IsUsed = invite.IsUsed,
            ExpiresAt = invite.ExpiresAt,
            CreatedAt = invite.CreatedAt,
        });
    }

    /// <summary>
    /// Redeem an invite token.
    /// </summary>
    [HttpPost("{token}/redeem")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult> RedeemInvite(string token, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var invite = await db.AlertInvites
            .FirstOrDefaultAsync(i => i.Token == token, ct);

        if (invite is null)
            return NotFound();

        if (invite.IsUsed)
            return Problem(detail: "Invite has already been redeemed", statusCode: 410, title: "Gone");

        if (invite.ExpiresAt < DateTime.UtcNow)
            return Problem(detail: "Invite has expired", statusCode: 410, title: "Gone");

        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null)
            return Unauthorized();

        invite.IsUsed = true;
        invite.UsedBy = subjectId.Value;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Revoke an unredeemed invite.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RevokeInvite(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var invite = await db.AlertInvites
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (invite is null)
            return NotFound();

        if (invite.IsUsed)
            return Problem(detail: "Cannot revoke an already-redeemed invite", statusCode: 409, title: "Conflict");

        db.AlertInvites.Remove(invite);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }
}

#region DTOs

public class AlertInviteResponse
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public Guid AlertRuleChannelId { get; set; }
    public string PermissionScope { get; set; } = string.Empty;
    public bool IsUsed { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateAlertInviteRequest
{
    public Guid AlertRuleChannelId { get; set; }
    public string? PermissionScope { get; set; }
}

#endregion
