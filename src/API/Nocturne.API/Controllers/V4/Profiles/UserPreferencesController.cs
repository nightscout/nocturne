using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Controller for managing user preferences.
/// </summary>
/// <seealso cref="NocturneDbContext"/>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/user/preferences")]
public class UserPreferencesController : ControllerBase
{
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<UserPreferencesController> _logger;

    // Supported language codes - must match supportedLocales.json
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "es", "fr", "de", "it", "pt", "nl", "ru", "zh", "ja", "ko"
    };

    /// <summary>
    /// Initializes a new instance of <see cref="UserPreferencesController"/>.
    /// </summary>
    /// <param name="dbContext">Database context for subject entity access.</param>
    /// <param name="logger">Logger instance.</param>
    public UserPreferencesController(
        NocturneDbContext dbContext,
        ILogger<UserPreferencesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Get the current user's preferences
    /// </summary>
    /// <returns>User preferences</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(UserPreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserPreferencesResponse>> GetPreferences()
    {
        var authContext = HttpContext.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated || !authContext.SubjectId.HasValue)
        {
            return Problem(detail: "Not authenticated", statusCode: 401, title: "Unauthorized");
        }

        var subject = await _dbContext.Subjects
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == authContext.SubjectId.Value);

        if (subject == null)
        {
            return Problem(detail: "Subject not found", statusCode: 401, title: "Unauthorized");
        }

        return Ok(new UserPreferencesResponse
        {
            PreferredLanguage = subject.PreferredLanguage
        });
    }

    /// <summary>
    /// Update the current user's preferences
    /// </summary>
    /// <param name="request">The preferences to update</param>
    /// <returns>Updated preferences</returns>
    [HttpPatch]
    [RemoteCommand]
    [ProducesResponseType(typeof(UserPreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserPreferencesResponse>> UpdatePreferences(
        [FromBody] UpdateUserPreferencesRequest request)
    {
        var authContext = HttpContext.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated || !authContext.SubjectId.HasValue)
        {
            return Problem(detail: "Not authenticated", statusCode: 401, title: "Unauthorized");
        }

        // Validate language code if provided
        if (request.PreferredLanguage != null && !SupportedLanguages.Contains(request.PreferredLanguage))
        {
            return BadRequest(new
            {
                error = "invalid_language",
                message = $"Language '{request.PreferredLanguage}' is not supported. Supported languages: {string.Join(", ", SupportedLanguages)}"
            });
        }

        var subject = await _dbContext.Subjects
            .FirstOrDefaultAsync(s => s.Id == authContext.SubjectId.Value);

        if (subject == null)
        {
            return Problem(detail: "Subject not found", statusCode: 401, title: "Unauthorized");
        }

        // Update preferences
        if (request.PreferredLanguage != null)
        {
            subject.PreferredLanguage = request.PreferredLanguage;
        }

        subject.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Updated preferences for subject {SubjectId}: PreferredLanguage={PreferredLanguage}",
            subject.Id,
            subject.PreferredLanguage);

        return Ok(new UserPreferencesResponse
        {
            PreferredLanguage = subject.PreferredLanguage
        });
    }
}

/// <summary>
/// User preferences response
/// </summary>
public class UserPreferencesResponse
{
    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    public string? PreferredLanguage { get; set; }
}

/// <summary>
/// Request to update user preferences
/// </summary>
public class UpdateUserPreferencesRequest
{
    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    public string? PreferredLanguage { get; set; }
}
