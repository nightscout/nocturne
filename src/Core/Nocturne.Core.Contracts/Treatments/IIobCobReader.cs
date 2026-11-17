using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Insulin and carbs on board at an instant, read from the stored treatments and the AID devices,
/// for every endpoint that reports one figure rather than a series.
/// </summary>
public interface IIobCobReader
{
    /// <summary>Hours of carb intakes read.</summary>
    const int CarbLookbackHours = 6;

    /// <summary>
    /// The treatments that can still be on board at <paramref name="at"/>: boluses dated in the
    /// look-back before it, temp basals that began early enough to still be running in it, and none
    /// after it.
    /// </summary>
    Task<IobCobInputs> ReadInputsAsync(long at, CancellationToken ct = default);

    /// <summary>
    /// <see cref="IIobCalculator.CalculateTotalAsync"/> and <see cref="ICobCalculator.CalculateTotalAsync"/>
    /// over <see cref="ReadInputsAsync"/>, whether or not the window holds any treatment, so a device
    /// snapshot alone still gives its totals.
    /// </summary>
    /// <param name="live">Whether <paramref name="at"/> is the present, as a live endpoint reads it:
    /// see <c>DeviceReportedValues</c>. Only a live read is cached.</param>
    Task<IobCobReading> ReadTotalAsync(long at, bool live, CancellationToken ct = default);
}

/// <summary>The treatments the totals are calculated from.</summary>
public sealed record IobCobInputs(List<Bolus> Boluses, List<TempBasal> TempBasals, List<CarbIntake> CarbIntakes);

/// <summary>The totals at an instant and the treatments they were calculated from.</summary>
/// <param name="Cob">Null when the COB calculation failed, so a COB failure does not take the IOB
/// with it.</param>
/// <remarks>A live reading is shared through the cache: read it, never change it.</remarks>
public sealed record IobCobReading(IobResult Iob, CobResult? Cob, IobCobInputs Inputs)
{
    /// <summary>
    /// Whether a bolus or a temp basal that reaches the insulin window stood behind <see cref="Iob"/>.
    /// Set by the reader, which knows the window: <see cref="Inputs"/> holds temp basals from before it.
    /// </summary>
    public bool InsulinInWindow { get; init; }

    /// <summary>
    /// Whether anything stood behind <see cref="Iob"/>: a device total, or insulin in the window.
    /// Without it the total is unknown, not zero.
    /// </summary>
    public bool HasIob => Iob.ReportedByDevice || InsulinInWindow;

    /// <summary>
    /// Whether anything stood behind <see cref="Cob"/>: a device total, or a carb intake in the window.
    /// </summary>
    public bool HasCob => Cob is not null && (Cob.ReportedByDevice || Inputs.CarbIntakes.Count > 0);
}
