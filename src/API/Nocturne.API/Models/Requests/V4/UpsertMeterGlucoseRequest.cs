namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a meter glucose reading via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.UpsertMeterGlucoseRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Glucose.MeterGlucoseController"/>
public class UpsertMeterGlucoseRequest
{
    /// <summary>
    /// When the meter reading was taken.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the glucose meter device.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Optional reference to the registered <see cref="Nocturne.Core.Models.V4.PatientDevice"/> that
    /// produced the reading. Must resolve to one of the caller's registered devices. When omitted on
    /// create, the server attempts attribution from <see cref="Device"/> and <see cref="DataSource"/>;
    /// when omitted on update, the existing link is preserved. Send the empty GUID
    /// (<c>00000000-0000-0000-0000-000000000000</c>) to state that the reading came from no registered
    /// device: the link is cleared and server-side attribution is skipped for this request.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Glucose reading in mg/dL (validated 0-10,000).
    /// </summary>
    public double Mgdl { get; set; }
}
