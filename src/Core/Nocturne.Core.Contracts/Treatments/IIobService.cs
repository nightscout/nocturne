using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Service for calculating Insulin on Board (IOB) with 1:1 legacy JavaScript compatibility.
/// Implements exact algorithms from ClientApp/lib/plugins/iob.js and ClientApp/src/lib/calculations/iob.ts.
/// </summary>
/// <remarks>
/// IOB is aggregated from three sources: bolus <see cref="Treatment"/> records, V4 <see cref="TempBasal"/>
/// records, and <see cref="DeviceStatus"/> entries (which carry loop-reported IOB).
/// Profile data (DIA, sensitivity, basal rate) is resolved via constructor-injected V4 resolvers.
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="DeviceStatus"/>
/// <seealso cref="TempBasal"/>
/// <seealso cref="IobResult"/>
public interface IIobService
{
    /// <summary>
    /// Calculate total IOB from all sources: bolus treatments, V4 temp basals, and device status.
    /// </summary>
    /// <param name="treatments">Bolus <see cref="Treatment"/> records.</param>
    /// <param name="deviceStatus"><see cref="DeviceStatus"/> entries (may contain loop-reported IOB).</param>
    /// <param name="time">Optional calculation time in Unix milliseconds. Defaults to now.</param>
    /// <param name="specProfile">Optional specific profile name to use.</param>
    /// <param name="tempBasals">Optional V4 <see cref="TempBasal"/> records.</param>
    /// <returns>Aggregated <see cref="IobResult"/> from all sources.</returns>
    IobResult CalculateTotal(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatus,
        long? time = null,
        string? specProfile = null,
        List<TempBasal>? tempBasals = null
    );

    /// <summary>
    /// Calculate IOB from bolus <see cref="Treatment"/> records only.
    /// </summary>
    /// <param name="treatments">Bolus <see cref="Treatment"/> records.</param>
    /// <param name="time">Optional calculation time in Unix milliseconds. Defaults to now.</param>
    /// <param name="specProfile">Optional specific profile name.</param>
    /// <returns><see cref="IobResult"/> from treatments.</returns>
    IobResult FromTreatments(
        List<Treatment> treatments,
        long? time = null,
        string? specProfile = null
    );

    /// <summary>
    /// Calculate IOB from V4 <see cref="TempBasal"/> records only.
    /// </summary>
    /// <param name="tempBasals">V4 <see cref="TempBasal"/> records.</param>
    /// <param name="time">Optional calculation time in Unix milliseconds. Defaults to now.</param>
    /// <param name="specProfile">Optional specific profile name.</param>
    /// <returns><see cref="IobResult"/> from temp basals.</returns>
    IobResult FromTempBasals(
        List<TempBasal> tempBasals,
        long? time = null,
        string? specProfile = null
    );

    /// <summary>
    /// Extract IOB from a single <see cref="DeviceStatus"/> entry (loop-reported IOB).
    /// </summary>
    /// <param name="deviceStatusEntry">A <see cref="DeviceStatus"/> entry containing loop IOB data.</param>
    /// <returns><see cref="IobResult"/> extracted from the device status.</returns>
    IobResult FromDeviceStatus(DeviceStatus deviceStatusEntry);

    /// <summary>
    /// Get the most recent loop-reported IOB from <see cref="DeviceStatus"/> entries before the given time.
    /// </summary>
    /// <param name="deviceStatus">List of <see cref="DeviceStatus"/> entries.</param>
    /// <param name="time">Timestamp in Unix milliseconds to search before.</param>
    /// <returns>Most recent <see cref="IobResult"/> from device status.</returns>
    IobResult LastIobDeviceStatus(List<DeviceStatus> deviceStatus, long time);

    /// <summary>
    /// Calculate IOB contribution from a single bolus <see cref="Treatment"/>.
    /// </summary>
    /// <param name="treatment">A bolus <see cref="Treatment"/>.</param>
    /// <param name="time">Optional calculation time in Unix milliseconds. Defaults to now.</param>
    /// <param name="specProfile">Optional specific profile name.</param>
    /// <returns><see cref="IobContribution"/> from this treatment.</returns>
    IobContribution CalcTreatment(
        Treatment treatment,
        long? time = null,
        string? specProfile = null
    );

    /// <summary>
    /// Calculate IOB contribution from a single basal <see cref="Treatment"/> (legacy format).
    /// </summary>
    /// <param name="treatment">A basal <see cref="Treatment"/>.</param>
    /// <param name="time">Optional calculation time in Unix milliseconds. Defaults to now.</param>
    /// <param name="specProfile">Optional specific profile name.</param>
    /// <returns><see cref="IobContribution"/> from this basal treatment.</returns>
    IobContribution CalcBasalTreatment(
        Treatment treatment,
        long? time = null,
        string? specProfile = null
    );

    /// <summary>
    /// Calculate IOB contribution from a single V4 <see cref="TempBasal"/>.
    /// </summary>
    /// <param name="tempBasal">A V4 <see cref="TempBasal"/> record.</param>
    /// <param name="time">Optional calculation time in Unix milliseconds. Defaults to now.</param>
    /// <param name="specProfile">Optional specific profile name.</param>
    /// <returns><see cref="IobContribution"/> from this temp basal.</returns>
    IobContribution CalcTempBasalIob(
        TempBasal tempBasal,
        long? time = null,
        string? specProfile = null
    );
}
