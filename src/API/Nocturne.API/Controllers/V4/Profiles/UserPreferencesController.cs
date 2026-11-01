using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Controller for managing user preferences.
/// </summary>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/user/preferences")]
[Authorize]
public class UserPreferencesController : ControllerBase
{
    private readonly ISubjectRepository _subjects;
    private readonly ILogger<UserPreferencesController> _logger;

    // Supported language codes - must match supportedLocales.json
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "es", "fr", "de", "it", "pt", "nl", "ru", "zh", "ja", "ko"
    };

    public UserPreferencesController(
        ISubjectRepository subjects,
        ILogger<UserPreferencesController> logger)
    {
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
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

        var subject = await _subjects.GetAsync(authContext.SubjectId.Value, HttpContext.RequestAborted);

        if (subject == null)
        {
            return Problem(detail: "Subject not found", statusCode: 401, title: "Unauthorized");
        }

        return Ok(new UserPreferencesResponse
        {
            PreferredLanguage = subject.PreferredLanguage,
            Preferences = UserDisplayPreferences.Deserialize(subject.Preferences)
        });
    }

    /// <summary>
    /// Update the current user's preferences
    /// </summary>
    /// <param name="request">The preferences to update</param>
    /// <returns>Updated preferences</returns>
    [HttpPatch]
    [DenyDemoSubject]
    [RemoteCommand(Invalidates = ["GetPreferences"])]
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
            return Problem(
                detail: $"Language '{request.PreferredLanguage}' is not supported. Supported languages: {string.Join(", ", SupportedLanguages)}",
                statusCode: 400,
                title: "Bad Request");
        }

        // Validate the constrained display-preference values if provided.
        if (request.Preferences?.Validate() is { } validationError)
        {
            return Problem(detail: validationError, statusCode: 400, title: "Bad Request");
        }

        var subjectId = authContext.SubjectId.Value;
        var stored = await _subjects.GetAsync(subjectId, HttpContext.RequestAborted);

        if (stored == null)
        {
            return Problem(detail: "Subject not found", statusCode: 401, title: "Unauthorized");
        }

        // Merge the partial display preferences over the stored blob so unset fields are preserved.
        string? mergedPreferences = null;
        if (request.Preferences != null)
        {
            var merged = UserDisplayPreferences.Deserialize(stored.Preferences);
            merged.MergeWith(request.Preferences);
            mergedPreferences = merged.Serialize();
        }

        await _subjects.UpdateAsync(
            subjectId,
            new SubjectEdit(PreferredLanguage: request.PreferredLanguage, Preferences: mergedPreferences),
            HttpContext.RequestAborted);

        var preferredLanguage = request.PreferredLanguage ?? stored.PreferredLanguage;
        var preferences = mergedPreferences ?? stored.Preferences;

        _logger.LogInformation(
            "Updated preferences for subject {SubjectId}: PreferredLanguage={PreferredLanguage}",
            subjectId,
            preferredLanguage);

        return Ok(new UserPreferencesResponse
        {
            PreferredLanguage = preferredLanguage,
            Preferences = UserDisplayPreferences.Deserialize(preferences)
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

    /// <summary>
    /// Per-user display preferences (units, time format, theme, chart style, etc.).
    /// Always present; unset fields are null.
    /// </summary>
    public UserDisplayPreferences Preferences { get; set; } = new();
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

    /// <summary>
    /// Partial display preferences to merge over the stored value. Only non-null fields are applied.
    /// </summary>
    public UserDisplayPreferences? Preferences { get; set; }
}
