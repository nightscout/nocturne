using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Platform administrator rights of a user account. Grant or remove the right for one account. The
/// last active platform administrator cannot be removed.
/// </summary>
/// <remarks>
/// Grants and removes the platform admin role of a subject.
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
    private readonly ISubjectRepository _subjects;

    public SubjectAdminController(ISubjectRepository subjects) => _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));

    /// <summary>
    /// Grant or revoke platform admin for a subject.
    /// Revoking is refused while the subject is the last active platform admin.
    /// </summary>
    [HttpPut("{id:guid}/platform-admin")]
    public async Task<IActionResult> SetPlatformAdmin(Guid id, [FromBody] SetPlatformAdminRequest request)
    {
        return await _subjects.UpdateAsync(id, new SubjectEdit(IsPlatformAdmin: request.IsPlatformAdmin), HttpContext.RequestAborted) switch
        {
            SubjectChange.NotFound => NotFound(),
            SubjectChange.LastActivePlatformAdmin => Conflict(new
            {
                error = "last_platform_admin",
                message = "Cannot demote the last active platform admin. Promote or activate another user first."
            }),
            _ => NoContent(),
        };
    }
}

public record SetPlatformAdminRequest(bool IsPlatformAdmin);
