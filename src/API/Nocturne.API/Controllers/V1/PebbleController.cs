using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Pebble endpoint providing 1:1 compatibility with Nightscout's /pebble endpoint.
/// Used by smartwatch apps, Loop, and other CGM monitoring applications.
/// Based on the legacy pebble.js implementation.
/// </summary>
/// <remarks>
/// The action requires only <c>glucose.read</c> while the response also carries IOB and COB, from the
/// AID system or treatments (<see cref="IIobCobReader"/>), and the uploader battery. This is
/// the accepted narrowing described on <see cref="V2.PropertiesController"/>, which names the
/// per-category follow-up.
/// </remarks>
/// <seealso cref="IEntryService"/>
/// <seealso cref="IIobCobReader"/>
[ApiController]
[Tags("V1")]
[Route("")]
public class PebbleController : ControllerBase
{
    private readonly IEntryService _entryService;
    private readonly IIobCobReader _iobCobReader;
    private readonly IUploaderSnapshotRepository _uploaderSnapshots;
    private readonly ITenantGlucoseUnits _glucoseUnits;
    private readonly ILogger<PebbleController> _logger;

    /// <summary>
    /// Largest <c>count</c> honoured: 24 hours of readings at 5 minute intervals.
    /// </summary>
    public const int MaxCount = 288;

    private const int UploaderLookback = 20;

    /// <summary>
    /// Initializes a new instance of <see cref="PebbleController"/>.
    /// </summary>
    /// <param name="entryService">Service for glucose entry retrieval.</param>
    /// <param name="iobCobReader">Reader of the IOB and COB now.</param>
    /// <param name="uploaderSnapshots">Repository of uploader snapshots, for the battery.</param>
    /// <param name="glucoseUnits">Resolver of the tenant's glucose units, for the default units.</param>
    /// <param name="logger">Logger instance.</param>
    public PebbleController(
        IEntryService entryService,
        IIobCobReader iobCobReader,
        IUploaderSnapshotRepository uploaderSnapshots,
        ITenantGlucoseUnits glucoseUnits,
        ILogger<PebbleController> logger
    )
    {
        _entryService = entryService;
        _iobCobReader = iobCobReader;
        _uploaderSnapshots = uploaderSnapshots;
        _glucoseUnits = glucoseUnits;
        _logger = logger;
    }

    /// <summary>
    /// Get pebble-formatted data for smartwatch apps and CGM monitors
    /// Returns current glucose, trend, delta, battery, IOB, and COB information
    /// </summary>
    /// <param name="units">Units for glucose display (mg/dl or mmol); the tenant's display units when omitted</param>
    /// <param name="count">Number of glucose readings to return (default: 1, at most <see cref="MaxCount"/>)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Pebble-formatted response with bgs, cals, and status</returns>
    [HttpGet("pebble")]
    [NightscoutEndpoint("/pebble")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(PebbleResponse), 200)]
    [RequireScope(Scope.GlucoseRead)]
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
            var useMmol = await ResolveUseMmolAsync(units, cancellationToken);
            count = Math.Clamp(count, 1, MaxCount);

            // Fetch required data in parallel
            var entriesTask = _entryService.GetEntriesAsync(type: "sgv", count: count + 1, skip: 0, cancellationToken);
            var calsTask = _entryService.GetEntriesAsync(type: "cal", count: count, skip: 0, cancellationToken);
            var iobCobTask = ReadIobCobAsync(cancellationToken);
            var batteryTask = ReadBatteryAsync(cancellationToken);

            await Task.WhenAll(entriesTask, calsTask, iobCobTask, batteryTask);

            var entries = (await entriesTask).ToArray();
            var iobCob = await iobCobTask;
            var battery = await batteryTask;
            var cals = (await calsTask).ToArray();

            // Build bgs array
            var bgs = new List<PebbleBg>();
            for (int i = 0; i < Math.Min(count, entries.Length); i++)
            {
                var entry = entries[i];
                var bg = new PebbleBg
                {
                    Sgv = FormatGlucose(entry.GlucoseMgdl(), useMmol),
                    Trend = entry.Trend ?? DirectionExtensions.ParseToTrendNumber(entry.Direction),
                    Direction = entry.Direction ?? "NONE",
                    Datetime = entry.Mills
                };

                // Add extra data to first entry (legacy compatibility)
                if (i == 0)
                {
                    var delta = DirectionService.CalculateDelta(entries, useMmol ? "mmol" : "mg/dl");
                    bg.Bgdelta = new PebbleBgdelta(
                        (useMmol ? delta?.Scaled : delta?.Mgdl) ?? 0,
                        useMmol
                    );

                    bg.Battery = battery;
                    bg.Iob = iobCob?.Iob.Display;
                    bg.Cob = iobCob?.Cob?.Display;
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

    private async Task<IobCobReading?> ReadIobCobAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _iobCobReader.ReadTotalAsync(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), live: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pebble IOB and COB unavailable");
            return null;
        }
    }

    private async Task<string?> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var newest = await _uploaderSnapshots.GetAsync(
                from: null, to: null, device: null, source: null,
                limit: UploaderLookback, offset: 0, descending: true, ct: cancellationToken
            );
            return newest.FirstOrDefault(u => u.Battery >= 0)?.Battery?.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pebble battery unavailable");
            return null;
        }
    }

    private async Task<bool> ResolveUseMmolAsync(string? units, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(units))
            return GlucoseUnits.IsMmol(units);

        return await _glucoseUnits.UsesMmolAsync(cancellationToken);
    }

    private static string FormatGlucose(double mgdl, bool useMmol)
    {
        if (useMmol)
        {
            return (mgdl / GlucoseConstants.MgdlPerMmol).ToString("F1");
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
    /// Delta from <see cref="DirectionService.CalculateDelta"/>; a JSON number in mg/dL and a
    /// one decimal string in mmol
    /// </summary>
    public PebbleBgdelta? Bgdelta { get; set; }

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

/// <summary>
/// Glucose delta in the units the response was requested in.
/// </summary>
/// <param name="Value">The delta in <paramref name="Mmol"/> units.</param>
/// <param name="Mmol">Whether <paramref name="Value"/> is mmol/L rather than mg/dL.</param>
[JsonConverter(typeof(PebbleBgdeltaConverter))]
public sealed record PebbleBgdelta(double Value, bool Mmol);

/// <summary>
/// Writes mg/dL as a JSON number and mmol as a one decimal string, as Nightscout's pebble does.
/// </summary>
public sealed class PebbleBgdeltaConverter : JsonConverter<PebbleBgdelta>
{
    /// <inheritdoc />
    public override PebbleBgdelta Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        PebbleBgdelta value,
        JsonSerializerOptions options
    )
    {
        if (value.Mmol)
            writer.WriteStringValue((value.Value + 0.0).ToString("F1", CultureInfo.InvariantCulture));
        else
            writer.WriteNumberValue(value.Value);
    }
}
