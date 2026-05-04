using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// Projects V4 glucose models back into the legacy <see cref="Entry"/> shape
/// for V1/V3 API compatibility.
/// </summary>
public static class EntryProjection
{
    /// <summary>
    /// Projects a <see cref="SensorGlucose"/> to an <see cref="Entry"/> with Type="sgv".
    /// </summary>
    public static Entry FromSensorGlucose(SensorGlucose sg)
    {
        var entry = CreateBase(sg.Id, sg.LegacyId, sg.Timestamp, sg.ModifiedAt, sg.Device, sg.App, sg.DataSource, sg.UtcOffset);
        entry.Type = "sgv";
        entry.Mgdl = sg.Mgdl;
        entry.Sgv = sg.Mgdl;
        entry.Mmol = sg.Mmol;
        entry.Direction = sg.Direction?.ToString();
        entry.Trend = sg.Trend.HasValue ? (int)sg.Trend.Value : null;
        entry.TrendRate = sg.TrendRate;
        entry.Noise = sg.Noise;
        entry.Filtered = sg.Filtered;
        entry.Unfiltered = sg.Unfiltered;
        entry.Delta = sg.Delta;
        return entry;
    }

    /// <summary>
    /// Projects a <see cref="MeterGlucose"/> to an <see cref="Entry"/> with Type="mbg".
    /// </summary>
    public static Entry FromMeterGlucose(MeterGlucose mg)
    {
        var entry = CreateBase(mg.Id, mg.LegacyId, mg.Timestamp, mg.ModifiedAt, mg.Device, mg.App, mg.DataSource, mg.UtcOffset);
        entry.Type = "mbg";
        entry.Mgdl = mg.Mgdl;
        entry.Mbg = mg.Mgdl;
        return entry;
    }

    /// <summary>
    /// Projects a <see cref="Calibration"/> to an <see cref="Entry"/> with Type="cal".
    /// </summary>
    public static Entry FromCalibration(Calibration cal)
    {
        var entry = CreateBase(cal.Id, cal.LegacyId, cal.Timestamp, cal.ModifiedAt, cal.Device, cal.App, cal.DataSource, cal.UtcOffset);
        entry.Type = "cal";
        entry.IsCalibration = true;
        entry.Slope = cal.Slope;
        entry.Intercept = cal.Intercept;
        entry.Scale = cal.Scale;
        return entry;
    }

    private static Entry CreateBase(
        Guid id,
        string? legacyId,
        DateTime timestamp,
        DateTime modifiedAt,
        string? device,
        string? app,
        string? dataSource,
        int? utcOffset)
    {
        var mills = new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return new Entry
        {
            Id = legacyId ?? id.ToString(),
            Mills = mills,
            DateString = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Device = device,
            App = app,
            DataSource = dataSource,
            UtcOffset = utcOffset,
            IsValid = true,
            SrvModified = new DateTimeOffset(modifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        };
    }
}
