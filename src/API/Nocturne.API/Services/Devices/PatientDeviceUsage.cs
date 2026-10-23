using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// The one rule that matches a record to a <see cref="PatientDevice"/>: its dates are local calendar
/// dates, so a record matches on the wearer's local day, not its UTC day.
/// </summary>
internal static class PatientDeviceUsage
{
    /// <summary>
    /// The local date of <paramref name="utc"/>: shifted by the record's own offset when it has one,
    /// else placed by <paramref name="clock"/>.
    /// </summary>
    public static DateOnly LocalDate(DateTime utc, int? utcOffsetMinutes, DeviceDateClock? clock) =>
        utcOffsetMinutes is { } offset
            ? DateOnly.FromDateTime(utc.AddMinutes(offset))
            : clock?.LocalDate(utc) ?? DateOnly.FromDateTime(utc);

    public static bool Covers(PatientDevice device, DateOnly localDate) =>
        (device.StartDate is null || device.StartDate <= localDate) &&
        (device.EndDate is null || device.EndDate >= localDate);
}
