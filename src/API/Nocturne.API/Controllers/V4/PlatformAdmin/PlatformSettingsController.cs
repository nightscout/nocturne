using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/platform-settings")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class PlatformSettingsController : ControllerBase
{
    private readonly PlatformSettingsService _service;

    public PlatformSettingsController(PlatformSettingsService service) => _service = service;

    /// <summary>
    /// Returns all platform setting categories with enabled status and configured field names.
    /// Secrets are never returned — only which fields have been set.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<PlatformSettingsSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var results = await _service.GetAllAsync();
        return Ok(results);
    }

    /// <summary>
    /// Returns a single platform setting category.
    /// </summary>
    [HttpGet("{category}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PlatformSettingsSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string category)
    {
        var result = await _service.GetAsync(category);
        if (result is null) return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// Upserts platform settings for a category. Blank fields preserve existing values.
    /// Returns restartRequired: true — the SvelteKit frontend must be restarted for changes to take effect.
    /// </summary>
    [HttpPut("{category}")]
    [RemoteCommand(Invalidates = ["GetAll", "Get"])]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Upsert(string category, [FromBody] UpsertPlatformSettingsRequest request)
    {
        var (success, errors) = await _service.UpsertAsync(category, request.Enabled, request.Fields);
        if (!success)
            return UnprocessableEntity(new { errors });
        return Ok(new { restartRequired = true });
    }

    /// <summary>
    /// Deletes platform settings for a category, removing all stored credentials.
    /// </summary>
    [HttpDelete("{category}")]
    [RemoteCommand(Invalidates = ["GetAll", "Get"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string category)
    {
        var deleted = await _service.DeleteAsync(category);
        if (!deleted) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// Returns decrypted credentials for all configured platforms.
    /// Restricted to instance-key authentication only (server-to-server).
    /// </summary>
    [HttpGet("decrypted")]
    [RequireInstanceKeyAuth]
    [ProducesResponseType(typeof(List<PlatformCredentials>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllDecrypted()
    {
        var results = await _service.GetAllDecryptedAsync();
        return Ok(results);
    }
}

public record UpsertPlatformSettingsRequest(bool Enabled, Dictionary<string, string> Fields);
