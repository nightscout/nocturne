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
/// Insulin on Board (IOB) calculation result aggregated across all active <see cref="Bolus"/> records.
/// </summary>
/// <seealso cref="Bolus"/>
public class IobResult
{
    /// <summary>Total insulin on board (units).</summary>
    public double Iob { get; set; }

    /// <summary>Total insulin activity (units/hr).</summary>
    public double? Activity { get; set; }

    /// <summary>Most recent <see cref="Bolus"/>.</summary>
    public Bolus? LastBolus { get; set; }

    /// <summary>Source of the IOB calculation (e.g., "iob", "openaps").</summary>
    public string? Source { get; set; }

    /// <summary>Device that reported the IOB data.</summary>
    public string? Device { get; set; }

    /// <summary>Timestamp of the IOB calculation (Unix milliseconds).</summary>
    public long? Mills { get; set; }

    /// <summary>Basal component of IOB (units).</summary>
    public double? BasalIob { get; set; }

    /// <summary>Treatment (bolus) component of IOB (units).</summary>
    public double? TreatmentIob { get; set; }

    /// <summary>Formatted display string for IOB value.</summary>
    public string? Display { get; set; }

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
