using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.CoachMarks;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// Says whether the caller should be prompted to add a backup way to sign in.
/// </summary>
/// <remarks>
/// Never shown to platform-access operators or service credentials, whose subject is not the
/// account being asked about.
/// </remarks>
[ApiController]
[Tags("Identity")]
[Route("api/v4/me/backup-sign-in-prompt")]
[Authorize]
[Produces("application/json")]
public class BackupSignInPromptController : ControllerBase
{
    private readonly ISubjectService _subjectService;
    private readonly ICoachMarkService _coachMarkService;

    /// <summary>
    /// Initializes a new instance of <see cref="BackupSignInPromptController"/>.
    /// </summary>
    public BackupSignInPromptController(
        ISubjectService subjectService,
        ICoachMarkService coachMarkService)
    {
        _subjectService = subjectService;
        _coachMarkService = coachMarkService;
    }

    /// <summary>
    /// Whether the caller has exactly one way to sign in and has not dismissed the prompt.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(BackupSignInPromptResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BackupSignInPromptResponse>> GetBackupSignInPrompt(
        CancellationToken cancellationToken)
    {
        var auth = HttpContext.GetAuthContext();
        if (auth?.SubjectId is not { } subjectId
            || auth.AuthType is AuthType.PlatformAccess or AuthType.ApiKey or AuthType.InstanceKey)
            return Ok(new BackupSignInPromptResponse());

        if (!await _subjectService.HasSingleSignInMethodAsync(subjectId))
            return Ok(new BackupSignInPromptResponse());

        var mark = await _coachMarkService.GetAsync(CoachMarkKeys.BackupSignInPrompt, cancellationToken);
        return Ok(new BackupSignInPromptResponse { Show = mark?.Status != "dismissed" });
    }
}

/// <summary>
/// Whether to prompt the caller to add a backup way to sign in.
/// </summary>
public class BackupSignInPromptResponse
{
    /// <summary>True when the account has a single sign-in method and the prompt is not dismissed.</summary>
    public bool Show { get; set; }
}
