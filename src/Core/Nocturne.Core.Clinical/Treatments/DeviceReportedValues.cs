using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// Shared rules for preferring device-reported IOB/COB over locally-computed values.
/// </summary>
/// <remarks>
/// <para>
/// An AID system (Loop, OpenAPS, AAPS, Trio) uploads the IOB and COB it acted on, and a pump reports
/// its own active insulin. Nocturne's own curve is an approximation of those, so where a recent
/// device value exists it is the better answer and every display path should agree on which one
/// that is. <see cref="IobCalculator"/>, <see cref="CobCalculator"/> and <see cref="IobCobSeries"/>
/// all choose through <see cref="DeviceReadings.At"/>, so the status pill, alerts, the chart and
/// <c>/api/v4/summary</c> cannot disagree about which value holds at an instant.
/// </para>
/// <para>
/// IOB and COB are chosen apart: each takes the first source that reports it, the APS snapshot,
/// then the pump snapshot, then the calculation. Of each source only the newest snapshot in the
/// window counts. The window reaches <see cref="RecencyThresholdMs"/> back from the instant, and
/// past it only for a live reading, the newest instant, where a snapshot stamped up to
/// <see cref="FutureSkewToleranceMs"/> later is the device's clock running ahead of the server's.
/// At an instant already past, a later snapshot is later data.
/// </para>
/// </remarks>
public static class DeviceReportedValues
{
    /// <summary>
    /// How old a device snapshot may be and still be preferred over a locally-computed value.
    /// </summary>
    public const long RecencyThresholdMs = 30 * 60 * 1000;

    /// <summary>
    /// How far past a live instant a snapshot may be timestamped and still be accepted,
    /// absorbing clock skew between the uploading device and the server.
    /// </summary>
    public const long FutureSkewToleranceMs = 5 * 60 * 1000;

    /// <summary>The snapshots that count at <paramref name="at"/>, inclusive at both ends.</summary>
    public static (long From, long To) Window(long at, bool live) =>
        (at - RecencyThresholdMs, live ? at + FutureSkewToleranceMs : at);
}

/// <summary>Which device a reported value came from.</summary>
public enum DeviceSource
{
    Aps,
    Pump,
}

/// <summary>One device snapshot's IOB and COB, as <see cref="DeviceReadings"/> chooses between them.</summary>
/// <param name="Iob">The total IOB the device reported; for a pump, its bolus IOB when it reports no total.</param>
/// <param name="Name">The source name a result reports, e.g. "Loop", "OpenAPS" or "Pump".</param>
public readonly record struct DeviceReading(
    long Mills,
    DeviceSource Source,
    double? Iob,
    double? Cob = null,
    double? BolusIob = null,
    double? BasalIob = null,
    string? Name = null,
    string? Device = null)
{
    public static DeviceReading FromAps(ApsIobCobPoint point) =>
        new(point.Timestamp.ToMills(), DeviceSource.Aps, point.Iob, point.Cob);

    public static DeviceReading FromPump(PumpIobPoint point) =>
        new(point.Timestamp.ToMills(), DeviceSource.Pump, point.Iob ?? point.BolusIob, BolusIob: point.BolusIob, Name: "Pump");
}

/// <summary>The device readings that hold at one instant, each null where the calculation holds instead.</summary>
public readonly record struct DeviceIobCob(DeviceReading? Iob, DeviceReading? Cob);

/// <summary>
/// The APS and pump snapshots of a span, from which <see cref="At"/> chooses the device values that
/// hold at an instant by the rule <see cref="DeviceReportedValues"/> states.
/// </summary>
public sealed class DeviceReadings
{
    private readonly DeviceReading[] _aps;
    private readonly DeviceReading[] _pump;

    public DeviceReadings(IEnumerable<DeviceReading> aps, IEnumerable<DeviceReading> pump)
    {
        _aps = [.. aps.OrderBy(r => r.Mills)];
        _pump = [.. pump.OrderBy(r => r.Mills)];
    }

    public static DeviceReadings Of(IEnumerable<ApsIobCobPoint>? aps, IEnumerable<PumpIobPoint>? pump) =>
        new((aps ?? []).Select(DeviceReading.FromAps), (pump ?? []).Select(DeviceReading.FromPump));

    /// <param name="live">Whether <paramref name="at"/> is the newest instant, which admits device clock skew.</param>
    public DeviceIobCob At(long at, bool live)
    {
        var (from, to) = DeviceReportedValues.Window(at, live);
        var aps = Newest(_aps, from, to);
        var pump = Newest(_pump, from, to);

        DeviceReading? iob = aps?.Iob is not null ? aps : pump?.Iob is not null ? pump : null;
        DeviceReading? cob = aps?.Cob is not null ? aps : pump?.Cob is not null ? pump : null;
        return new DeviceIobCob(iob, cob);
    }

    private static DeviceReading? Newest(DeviceReading[] readings, long from, long to)
    {
        int lo = 0, hi = readings.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (readings[mid].Mills <= to)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo > 0 && readings[lo - 1].Mills >= from ? readings[lo - 1] : null;
    }
}
