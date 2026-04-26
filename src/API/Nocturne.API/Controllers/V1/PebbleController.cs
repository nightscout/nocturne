using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Pebble endpoint providing 1:1 compatibility with Nightscout's /pebble endpoint.
/// Used by smartwatch apps, Loop, and other CGM monitoring applications.
/// Based on the legacy pebble.js implementation.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="DeviceStatusProjectionService"/>
/// <seealso cref="ITreatmentService"/>
[ApiController]
[Route("")]
public class PebbleController : ControllerBase
{
    private readonly IEntryService _entryService;
    private readonly DeviceStatusProjectionService _projectionService;
    private readonly ITreatmentService _treatmentService;
    private readonly ILogger<PebbleController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PebbleController"/>.
    /// </summary>
    /// <param name="entryService">Service for glucose entry retrieval.</param>
    /// <param name="projectionService">Service for projecting device status from V4 snapshots.</param>
    /// <param name="treatmentService">Service for treatment data retrieval.</param>
    /// <param name="logger">Logger instance.</param>
    public PebbleController(
        IEntryService entryService,
        DeviceStatusProjectionService projectionService,
        ITreatmentService treatmentService,
        ILogger<PebbleController> logger
    )
    {
        _entryService = entryService;
        _projectionService = projectionService;
        _treatmentService = treatmentService;
        _logger = logger;
    }

    /// <summary>
    /// Get pebble-formatted data for smartwatch apps and CGM monitors
    /// Returns current glucose, trend, delta, battery, IOB, and COB information
    /// </summary>
    /// <param name="units">Units for glucose display (mg/dl or mmol)</param>
    /// <param name="count">Number of glucose readings to return (default: 1)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Pebble-formatted response with bgs, cals, and status</returns>
    [HttpGet("pebble")]
    [NightscoutEndpoint("/pebble")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(PebbleResponse), 200)]
    public async Task<ActionResult<PebbleResponse>> GetPebbleData(
        [FromQuery] string? units = null,
        [FromQuery] int count = 1,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Pebble endpoint requested with units: {Units}, count: {Count} from {RemoteIpAddress}",
            units,
            count,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var useMmol = string.Equals(units, "mmol", StringComparison.OrdinalIgnoreCase);
            count = Math.Max(1, Math.Min(count, 10)); // Limit to reasonable range

            // Fetch required data in parallel
            var entriesTask = _entryService.GetEntriesAsync(type: "sgv", count: count + 1, skip: 0, cancellationToken);
            var deviceStatusTask = _projectionService.GetAsync(count: 1, skip: 0, find: null, ct: cancellationToken);
            var calsTask = _entryService.GetEntriesAsync(type: "cal", count: count, skip: 0, cancellationToken);

            await Task.WhenAll(entriesTask, deviceStatusTask, calsTask);

            var entries = (await entriesTask).ToArray();
            var deviceStatuses = (await deviceStatusTask).ToArray();
            var cals = (await calsTask).ToArray();

            // Build bgs array
            var bgs = new List<PebbleBg>();
            for (int i = 0; i < Math.Min(count, entries.Length); i++)
            {
                var entry = entries[i];
                var bg = new PebbleBg
                {
                    Sgv = FormatGlucose(entry.Sgv ?? entry.Mgdl, useMmol),
                    Trend = entry.Trend ?? DirectionExtensions.ParseToTrendNumber(entry.Direction),
                    Direction = entry.Direction ?? "NONE",
                    Datetime = entry.Mills
                };

                // Add extra data to first entry (legacy compatibility)
                if (i == 0)
                {
                    // Calculate delta from previous entry
                    if (entries.Length > 1)
                    {
                        var currentBg = entry.Sgv ?? entry.Mgdl;
                        var previousBg = entries[1].Sgv ?? entries[1].Mgdl;
                        var delta = currentBg - previousBg;
                        bg.Bgdelta = useMmol
                            ? (delta / 18.0).ToString("F1")
                            : delta.ToString("F0");
                    }
                    else
                    {
                        bg.Bgdelta = "0";
                    }

                    // Get battery from device status
                    var uploaderStatus = deviceStatuses.FirstOrDefault();
                    if (uploaderStatus?.Uploader?.Battery != null)
                    {
                        bg.Battery = uploaderStatus.Uploader.Battery.ToString();
                    }

                    // Get IOB from device status (Loop stores it there)
                    if (uploaderStatus?.Loop?.Iob != null)
                    {
                        bg.Iob = uploaderStatus.Loop.Iob.Iob?.ToString("F2") ?? "0";
                    }

                    // Get COB from device status
                    if (uploaderStatus?.Loop?.Cob != null)
                    {
                        bg.Cob = uploaderStatus.Loop.Cob.Cob ?? 0;
                    }
                }

                bgs.Add(bg);
            }

            // Build cals array (calibrations)
            var calibrations = cals.Select(cal => new PebbleCal
            {
                Slope = cal.Slope,
                Intercept = cal.Intercept,
                Scale = cal.Scale
            }).ToList();

            var response = new PebbleResponse
            {
                Status = new List<PebbleStatus>
                {
                    new() { Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                },
                Bgs = bgs,
                Cals = calibrations
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating pebble response");
            return StatusCode(500, new PebbleResponse
            {
                Status = new List<PebbleStatus>
                {
                    new() { Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                },
                Bgs = new List<PebbleBg>(),
                Cals = new List<PebbleCal>()
            });
        }
    }

    private static string FormatGlucose(double mgdl, bool useMmol)
    {
        if (useMmol)
        {
            return (mgdl / 18.0).ToString("F1");
        }
        return mgdl.ToString("F0");
    }
}

/// <summary>
/// Pebble response model for 1:1 Nightscout compatibility
/// </summary>
public class PebbleResponse
{
    /// <summary>
    /// Status array containing current timestamp
    /// </summary>
    public List<PebbleStatus> Status { get; set; } = new();

    /// <summary>
    /// Blood glucose readings array
    /// </summary>
    public List<PebbleBg> Bgs { get; set; } = new();

    /// <summary>
    /// Calibration readings array
    /// </summary>
    public List<PebbleCal> Cals { get; set; } = new();
}

/// <summary>
/// Pebble status entry
/// </summary>
public class PebbleStatus
{
    /// <summary>
    /// Current timestamp in milliseconds since epoch
    /// </summary>
    public long Now { get; set; }
}

/// <summary>
/// Pebble blood glucose entry
/// </summary>
public class PebbleBg
{
    /// <summary>
    /// Sensor glucose value (formatted as string for legacy compatibility)
    /// </summary>
    public string Sgv { get; set; } = "";

    /// <summary>
    /// Numeric trend indicator (1-9)
    /// </summary>
    public int Trend { get; set; }

    /// <summary>
    /// Direction string (Flat, SingleUp, etc.)
    /// </summary>
    public string Direction { get; set; } = "";

    /// <summary>
    /// Timestamp in milliseconds since epoch
    /// </summary>
    public long Datetime { get; set; }

    /// <summary>
    /// Delta from previous reading (formatted string)
    /// </summary>
    public string? Bgdelta { get; set; }

    /// <summary>
    /// Battery percentage (formatted string)
    /// </summary>
    public string? Battery { get; set; }

    /// <summary>
    /// Insulin on board (formatted string)
    /// </summary>
    public string? Iob { get; set; }

    /// <summary>
    /// Bolus wizard preview (formatted string)
    /// </summary>
    public string? Bwp { get; set; }

    /// <summary>
    /// Bolus wizard preview outcome
    /// </summary>
    public double? Bwpo { get; set; }

    /// <summary>
    /// Carbs on board
    /// </summary>
    public double? Cob { get; set; }
}

/// <summary>
/// Pebble calibration entry
/// </summary>
public class PebbleCal
{
    /// <summary>
    /// Calibration slope
    /// </summary>
    public double? Slope { get; set; }

    /// <summary>
    /// Calibration intercept
    /// </summary>
    public double? Intercept { get; set; }

    /// <summary>
    /// Calibration scale
    /// </summary>
    public double? Scale { get; set; }
}
