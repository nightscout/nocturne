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

    public string? Display { get; set; }

    /// <summary>
    /// <paramref name="iob"/> as <see cref="Display"/> shows it, by Nightscout's <c>utils.toFixed</c>:
    /// "0" for zero, else two decimals, with a value that rounds to zero shown unsigned. Invariant, so
    /// the string never picks up the server's decimal separator.
    /// </summary>
    public static string FormatDisplay(double iob) =>
        iob == 0
            ? "0"
            : (Math.Round(iob, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Formatted display line including IOB details.</summary>
    public string? DisplayLine { get; set; }
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

    /// <summary>Temp basal rate (U/hr), 0 if no temp basal active.</summary>
    public double TempBasal { get; set; }

    /// <summary>Extended portion of combo bolus as basal delivery (U/hr).</summary>
    public double ComboBolusBasal { get; set; }

    /// <summary>Total effective basal rate: scheduled + temp basal + combo bolus (U/hr).</summary>
    public double TotalBasal { get; set; }
}
