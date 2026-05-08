using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Returns small "right now" therapy values consumed by the dashboard Halo Dial:
/// the active pump operational mode and the current insulin sensitivity expressed
/// as a percentage of the profile baseline.
/// </summary>
[ApiController]
[Tags("Current Therapy State")]
[Route("api/v4/current-therapy-state")]
[Authorize]
public class CurrentTherapyStateController : ControllerBase
{
    private readonly IStateSpanService _stateSpanService;
    private readonly ISensitivityResolver _sensitivityResolver;

    public CurrentTherapyStateController(
        IStateSpanService stateSpanService,
        ISensitivityResolver sensitivityResolver)
    {
        _stateSpanService = stateSpanService;
        _sensitivityResolver = sensitivityResolver;
    }

    /// <summary>
    /// Get the current pump mode and sensitivity adjustment for the active tenant.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(CurrentTherapyStateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentTherapyStateResponse>> GetCurrentTherapyState(
        CancellationToken cancellationToken = default)
    {
        var pumpMode = await _stateSpanService.GetCurrentPumpModeAsync(cancellationToken);
        var sensitivityPercent = await _sensitivityResolver.GetCurrentSensitivityPercentAsync(cancellationToken);
        return Ok(new CurrentTherapyStateResponse
        {
            CurrentPumpMode = pumpMode,
            SensitivityPercent = sensitivityPercent,
        });
    }
}

/// <summary>
/// Snapshot of "right now" therapy state for the Halo Dial.
/// </summary>
public class CurrentTherapyStateResponse
{
    /// <summary>
    /// The active pump operational mode, derived from the most recently started
    /// open-ended <see cref="StateSpanCategory.PumpMode"/> span. Null when no
    /// pump-mode span is currently open.
    /// </summary>
    public PumpModeState? CurrentPumpMode { get; set; }

    /// <summary>
    /// Current effective ISF as a percentage of the schedule baseline.
    /// 100 = at baseline. Below 100 = active CCP makes the pump more aggressive.
    /// Null when no CircadianPercentageProfile adjustment is active.
    /// </summary>
    public double? SensitivityPercent { get; set; }
}
