using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Platform-admin controller for managing subject (user) accounts across the instance.
/// </summary>
/// <remarks>
/// Provides administrative operations on user accounts: listing, deactivation, and role changes.
/// Restricted to users with the <c>platform_admin</c> role. Allowed during initial setup so that
/// an administrator can manage accounts before normal login is possible.
/// </remarks>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/admin/subjects")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
[AllowDuringSetup]
public class SubjectAdminController : ControllerBase
{
    private readonly INocturneDbContextFactory _contexts;

    public SubjectAdminController(INocturneDbContextFactory contexts) => _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));

    /// <summary>
    /// Grant or revoke platform admin for a subject.
    /// Blocks self-demotion if the caller is the last platform admin.
    /// </summary>
    [HttpPut("{id:guid}/platform-admin")]
    public async Task<IActionResult> SetPlatformAdmin(Guid id, [FromBody] SetPlatformAdminRequest request)
    {
        await using var db = await _contexts.CreateAsync();
        var subject = await db.Subjects.FindAsync(id);
        if (subject is null) return NotFound();

        if (!request.IsPlatformAdmin)
        {
            // Block demotion if this is the last platform admin
            var adminCount = await db.Subjects.CountAsync(s => s.IsPlatformAdmin);
            if (adminCount <= 1 && subject.IsPlatformAdmin)
            {
                return Conflict(new
                {
                    error = "last_platform_admin",
                    message = "Cannot demote the last platform admin. Promote another user first."
                });
            }
        }

        subject.IsPlatformAdmin = request.IsPlatformAdmin;
        subject.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return NoContent();
    }
}

public record SetPlatformAdminRequest(bool IsPlatformAdmin);
