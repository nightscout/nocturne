using System.Globalization;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// IOB contribution from a single <see cref="Bolus"/> or <see cref="TempBasal"/>.
/// </summary>
public class IobContribution
{
    /// <summary>Insulin-on-board contribution (units).</summary>
    public double IobContrib { get; set; }

    /// <summary>Insulin activity contribution (units/hr).</summary>
    public double ActivityContrib { get; set; }
}

/// <summary>
/// Insulin on Board (IOB) from boluses and temp basals, calculated or as a device reported it.
/// </summary>
/// <seealso cref="Bolus"/>
public class IobResult
{
    /// <summary>
    /// Total insulin on board (units): bolus IOB plus net basal IOB, or a device's own total. It is
    /// negative while a low temp or suspend has withheld more insulin than is on board.
    /// </summary>
    public double Iob { get; set; }

    /// <summary>Insulin activity of the bolus and net basal insulin (units/hr).</summary>
    public double? Activity { get; set; }

    /// <summary>Most recent <see cref="Bolus"/>.</summary>
    public Bolus? LastBolus { get; set; }

    /// <summary>Source of the IOB calculation (e.g., "iob", "openaps").</summary>
    public string? Source { get; set; }

    public string? Device { get; set; }

    /// <summary>Whether <see cref="Iob"/> is a device's own total rather than a calculation, zero included.</summary>
    [JsonIgnore]
    public bool ReportedByDevice { get; set; }

    /// <summary>Timestamp of the IOB calculation (Unix milliseconds).</summary>
    public long? Mills { get; set; }

    /// <summary>
    /// Bolus part of <see cref="Iob"/> (units). Null when a device reported the total without it.
    /// </summary>
    public double? BolusIob { get; set; }

    /// <summary>
    /// Net basal part of <see cref="Iob"/> (units), negative below the schedule. Null when no temp
    /// basal delivered, or when a device reported the total without it.
    /// </summary>
    public double? BasalIob { get; set; }

    /// <summary>
    /// Beside a device's total, the bolus IOB calculated from the treatments (units), as Nightscout
    /// reports it. Null on a calculated result.
    /// </summary>
    public double? TreatmentIob { get; set; }

    /// <summary>
    /// <see cref="Iob"/> by Nightscout's <c>utils.toFixed</c>: "0" for zero, else
    /// <see cref="JsNumber.ToFixed2"/> with a negative value that rounds to zero unsigned. The
    /// calculator rounds <see cref="Iob"/> to three decimals first, as Nightscout does.
    /// </summary>
    public string Display =>
        Iob == 0 ? "0" : JsNumber.ToFixed2(Iob) switch { "-0.00" => "0.00", var text => text };

    public string DisplayLine => $"IOB: {Display}U";
}

/// <summary>
/// Temporary basal calculation result, combining scheduled basal with any active temp basal or combo bolus.
/// </summary>
/// <seealso cref="Treatment"/>
public class TempBasalResult
{
    /// <summary>Scheduled basal rate (U/hr).</summary>
    public double Basal { get; set; }

    /// <summary>Active temp basal <see cref="Treatment"/>, if any.</summary>
    public Treatment? Treatment { get; set; }

    /// <summary>Active combo bolus <see cref="Treatment"/>, if any.</summary>
    public Treatment? ComboBolusTreatment { get; set; }

    /// <summary>Temp basal rate (U/hr), the scheduled rate when no temp basal is active.</summary>
    public double TempBasal { get; set; }

    /// <summary>Extended portion of combo bolus as basal delivery (U/hr).</summary>
    public double ComboBolusBasal { get; set; }

    /// <summary>Total effective basal rate: scheduled + temp basal + combo bolus (U/hr).</summary>
    public double TotalBasal { get; set; }
}

/// <summary>JavaScript number formatting, for strings Nocturne must show as Nightscout does.</summary>
public static class JsNumber
{
    /// <summary>
    /// <c>value.toFixed(2)</c>: the exact binary value rounded to two decimals, so 0.015, stored just
    /// below its midpoint, gives "0.01". .NET's "F2" rounds the exact value too, but breaks an exact
    /// tie to even, where JavaScript rounds it away from zero. A tie at two decimals is an odd
    /// multiple of 1/8, which <c>value * 8</c> finds exactly.
    /// </summary>
    public static string ToFixed2(double value)
    {
        var rounded = Math.IEEERemainder(value * 8, 2) is 1 or -1
            ? ScaledRounding.Round(value, 2, MidpointRounding.AwayFromZero)
            : value;
        return rounded.ToString("F2", CultureInfo.InvariantCulture);
    }
}
