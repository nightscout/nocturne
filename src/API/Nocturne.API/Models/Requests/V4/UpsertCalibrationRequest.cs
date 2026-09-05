namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a CGM sensor calibration record via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.UpsertCalibrationRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Glucose.CalibrationController"/>
public class UpsertCalibrationRequest : IBulkUpsertRequest
{
    /// <summary>
    /// When the calibration was performed.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the CGM device being calibrated.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Linear calibration slope coefficient.
    /// </summary>
    public double? Slope { get; set; }

    /// <summary>
    /// Linear calibration intercept value.
    /// </summary>
    public double? Intercept { get; set; }

    /// <summary>
    /// Calibration scale factor.
    /// </summary>
    public double? Scale { get; set; }

    /// <summary>
    /// Always <c>null</c>: <see cref="Nocturne.Core.Models.V4.Calibration"/> carries no sync key, so a bulk
    /// payload of these has nothing for the upsert to match on.
    /// </summary>
    string? IBulkUpsertRequest.SyncIdentifier => null;
}
