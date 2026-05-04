using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Profiles;

[ApiController]
[Route("api/v4/settings/glucose-processing")]
[Authorize]
public class GlucoseProcessingSettingsController(
    IGlucoseProcessingConfigProvider configProvider) : ControllerBase
{
    [HttpGet("preference")]
    [RemoteQuery]
    [ProducesResponseType(typeof(GlucoseProcessingPreferenceResponse), 200)]
    public async Task<ActionResult<GlucoseProcessingPreferenceResponse>> GetPreference()
    {
        var preference = await configProvider.GetPreferredProcessingAsync(HttpContext.RequestAborted);
        return Ok(new GlucoseProcessingPreferenceResponse { PreferredGlucoseProcessing = preference?.ToString() });
    }

    [HttpPut("preference")]
    [RemoteCommand(Invalidates = [nameof(GetPreference)])]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SetPreference([FromBody] SetGlucoseProcessingPreferenceRequest request)
    {
        GlucoseProcessing? parsed = null;
        if (request.PreferredGlucoseProcessing is not null)
        {
            if (!Enum.TryParse<GlucoseProcessing>(request.PreferredGlucoseProcessing, ignoreCase: true, out var gp))
                return BadRequest("Invalid glucose processing value. Must be 'Smoothed' or 'Unsmoothed'.");
            parsed = gp;
        }

        await configProvider.SetPreferredProcessingAsync(parsed, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpGet("source-defaults")]
    [RemoteQuery]
    [ProducesResponseType(typeof(GlucoseProcessingSourceDefaultsResponse), 200)]
    public async Task<ActionResult<GlucoseProcessingSourceDefaultsResponse>> GetSourceDefaults()
    {
        var defaults = await configProvider.GetSourceDefaultsAsync(HttpContext.RequestAborted);
        return Ok(new GlucoseProcessingSourceDefaultsResponse { Rules = defaults });
    }

    [HttpPut("source-defaults")]
    [RemoteCommand(Invalidates = [nameof(GetSourceDefaults)])]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SetSourceDefaults([FromBody] SetGlucoseProcessingSourceDefaultsRequest request)
    {
        await configProvider.SetSourceDefaultsAsync(request.Rules ?? [], HttpContext.RequestAborted);
        return NoContent();
    }
}

public class GlucoseProcessingPreferenceResponse
{
    public string? PreferredGlucoseProcessing { get; set; }
}

public class SetGlucoseProcessingPreferenceRequest
{
    public string? PreferredGlucoseProcessing { get; set; }
}

public class GlucoseProcessingSourceDefaultsResponse
{
    public List<GlucoseProcessingSourceDefault> Rules { get; set; } = [];
}

public class SetGlucoseProcessingSourceDefaultsRequest
{
    public List<GlucoseProcessingSourceDefault>? Rules { get; set; }
}
