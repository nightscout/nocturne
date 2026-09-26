using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Controller for global MyFitnessPal matching settings.
/// </summary>
/// <seealso cref="IMyFitnessPalMatchingSettingsService"/>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/connectors/myfitnesspal/settings")]
[RequireScope(Scope.TenantSettings)]
public class MyFitnessPalSettingsController : ControllerBase
{
    private readonly IMyFitnessPalMatchingSettingsService _settingsService;

    /// <summary>
    /// Initializes a new instance of <see cref="MyFitnessPalSettingsController"/>.
    /// </summary>
    /// <param name="settingsService">Service for MyFitnessPal matching settings persistence.</param>
    public MyFitnessPalSettingsController(IMyFitnessPalMatchingSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get global MyFitnessPal matching settings.
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(MyFitnessPalMatchingSettings), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<MyFitnessPalMatchingSettings>> GetSettings()
    {
        // Defaults served for a read that failed are what the settings form then saves back.
        // <see cref="SettingsUnavailableException"/>.
        var settings = await _settingsService.GetSettingsAsync(HttpContext.RequestAborted);
        if (settings == null)
        {
            return Problem(
                detail: "Settings could not be read and have not been changed. Try again.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Settings Unavailable"
            );
        }

        return Ok(settings);
    }

    /// <summary>
    /// Update global MyFitnessPal matching settings.
    /// </summary>
    [HttpPut]
    [Authorize]
    [ProducesResponseType(typeof(MyFitnessPalMatchingSettings), 200)]
    public async Task<ActionResult<MyFitnessPalMatchingSettings>> SaveSettings(
        [FromBody] MyFitnessPalMatchingSettings settings
    )
    {
        if (settings == null)
        {
            return BadRequest();
        }

        var saved = await _settingsService.SaveSettingsAsync(
            settings,
            HttpContext.RequestAborted
        );
        return Ok(saved);
    }
}
