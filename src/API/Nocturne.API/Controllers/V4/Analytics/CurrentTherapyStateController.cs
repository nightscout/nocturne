using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Returns small "right now" therapy values for the dashboard: the active pump
/// operational mode, the current insulin sensitivity expressed as a percentage
/// of the profile baseline, the pump's latest reservoir and battery reading, and the
/// glucose thresholds in force now.
/// </summary>
[ApiController]
[Tags("Current Therapy State")]
[Route("api/v4/current-therapy-state")]
[Authorize]
public class CurrentTherapyStateController : ControllerBase
{
    private readonly IStateSpanService _stateSpanService;
    private readonly ISensitivityResolver _sensitivityResolver;
    private readonly IPumpSnapshotRepository _pumpSnapshotRepository;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ITargetRangeResolver _targetRangeResolver;

    public CurrentTherapyStateController(
        IStateSpanService stateSpanService,
        ISensitivityResolver sensitivityResolver,
        IPumpSnapshotRepository pumpSnapshotRepository,
        ITherapySettingsResolver therapySettingsResolver,
        ITargetRangeResolver targetRangeResolver)
    {
        _stateSpanService = stateSpanService;
        _sensitivityResolver = sensitivityResolver;
        _pumpSnapshotRepository = pumpSnapshotRepository;
        _therapySettingsResolver = therapySettingsResolver;
        _targetRangeResolver = targetRangeResolver;
    }

    /// <summary>
    /// Get the current pump mode, sensitivity adjustment, and latest pump
    /// reservoir/battery reading for the active tenant.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.GlucoseRead, Scope.DevicesRead, Scope.TherapyRead)]
    [ProducesResponseType(typeof(CurrentTherapyStateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentTherapyStateResponse>> GetCurrentTherapyState(
        CancellationToken cancellationToken = default)
    {
        var pumpMode = await _stateSpanService.GetCurrentPumpModeAsync(cancellationToken);
        var sensitivityPercent = await _sensitivityResolver.GetCurrentSensitivityPercentAsync(cancellationToken);
        var latestPump = await _pumpSnapshotRepository.GetLatestAsync(asOf: null, cancellationToken);
        var thresholds = await ChartThresholdsBuilder.BuildAsync(
            _targetRangeResolver,
            await _therapySettingsResolver.HasDataAsync(cancellationToken),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            cancellationToken);
        return Ok(CurrentTherapyStateReadScopeGuard.Redact(
            new CurrentTherapyStateResponse
            {
                CurrentPumpMode = pumpMode,
                SensitivityPercent = sensitivityPercent,
                Reservoir = latestPump?.Reservoir,
                PumpBatteryPercent = latestPump?.BatteryPercent,
                PumpBatteryVoltage = latestPump?.BatteryVoltage,
                Thresholds = thresholds,
            },
            HttpContext.GetGrantedScopes()));
    }
}

/// <summary>
/// Snapshot of "right now" therapy state.
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

    /// <summary>
    /// Insulin remaining in the pump reservoir (units), from the most recent pump
    /// snapshot. Null when no pump has reported a reservoir value — e.g. Omnipod
    /// pods report no numeric reservoir until they drop below 50 U.
    /// </summary>
    public double? Reservoir { get; set; }

    /// <summary>
    /// Pump battery level as a percentage (0–100) from the most recent pump
    /// snapshot, or null when the pump reports no battery percentage.
    /// </summary>
    public int? PumpBatteryPercent { get; set; }

    /// <summary>
    /// Pump battery voltage from the most recent pump snapshot, or null when the
    /// pump reports no battery voltage.
    /// </summary>
    public double? PumpBatteryVoltage { get; set; }

    /// <summary>
    /// The glucose colouring band and the personal target in force now, as the chart data
    /// endpoint carries them for a window ending now. <see cref="ChartThresholdsDto.GlucoseYMax"/>
    /// is the cap on the ceiling chart data derives from its series, there being no series here.
    /// </summary>
    public ChartThresholdsDto Thresholds { get; set; } = new();
}
