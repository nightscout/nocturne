using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Controller for managing user preferences.
/// </summary>
/// <seealso cref="NocturneDbContext"/>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/user/preferences")]
[Authorize]
public class UserPreferencesController : ControllerBase
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<UserPreferencesController> _logger;

    // Supported language codes - must match supportedLocales.json
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "es", "fr", "de", "it", "pt", "nl", "ru", "zh", "ja", "ko", "cs"
    };

    /// <summary>
    /// Initializes a new instance of <see cref="UserPreferencesController"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public UserPreferencesController(
        INocturneDbContextFactory contexts,
        ILogger<UserPreferencesController> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
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
        await using var db = await _contexts.CreateAsync();
        var authContext = HttpContext.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated || !authContext.SubjectId.HasValue)
        {
            return Problem(detail: "Not authenticated", statusCode: 401, title: "Unauthorized");
        }

        var subject = await db.Subjects
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == authContext.SubjectId.Value);

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
        await using var db = await _contexts.CreateAsync();
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

        var subject = await db.Subjects
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

        // Merge the partial display preferences over the stored blob so unset fields are preserved.
        if (request.Preferences != null)
        {
            var merged = UserDisplayPreferences.Deserialize(subject.Preferences);
            merged.MergeWith(request.Preferences);
            subject.Preferences = merged.Serialize();
        }

        subject.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Updated preferences for subject {SubjectId}: PreferredLanguage={PreferredLanguage}",
            subject.Id,
            subject.PreferredLanguage);

        return Ok(new UserPreferencesResponse
        {
            PreferredLanguage = subject.PreferredLanguage,
            Preferences = UserDisplayPreferences.Deserialize(subject.Preferences)
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
