using System.Text.Json.Serialization;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Kind of a manually reported reservoir value.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReservoirReportKind>))]
public enum ReservoirReportKind
{
    /// <summary>The current reservoir level as read from the pump (e.g. its on-screen display).</summary>
    Reading,

    /// <summary>A fresh fill: the reservoir/pod was just filled with the reported amount.</summary>
    Fill,
}

/// <summary>
/// Request body for reporting a known reservoir value via the V4 API.
/// </summary>
/// <remarks>
/// Covers pumps whose uploads don't carry a usable level — e.g. Omnipod Dash above
/// 50 units, or the Ypsopump, which never reports one. A <see cref="ReservoirReportKind.Fill"/>
/// report also records a reservoir-change device event, resetting reservoir-age tracking.
/// </remarks>
/// <seealso cref="Validators.V4.CreateReservoirReportRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Devices.ReservoirReportsController"/>
public class CreateReservoirReportRequest
{
    /// <summary>
    /// Insulin in the reservoir, in units.
    /// </summary>
    public double Units { get; set; }

    /// <summary>
    /// Whether this is a spot reading of the current level or a fresh fill.
    /// </summary>
    public ReservoirReportKind Kind { get; set; }

    /// <summary>
    /// When the value was observed. Defaults to the current time when omitted.
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the observation, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the pump the value belongs to.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }
}
