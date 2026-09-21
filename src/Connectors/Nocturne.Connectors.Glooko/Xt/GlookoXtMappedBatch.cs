using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>Everything one batch of Glooko XT records fans out into, one list per Nocturne record type.</summary>
public sealed class GlookoXtMappedBatch
{
    public List<SensorGlucose> SensorGlucose { get; } = [];
    public List<BGCheck> BGChecks { get; } = [];
    public List<Bolus> Boluses { get; } = [];
    public List<BolusCalculation> BolusCalculations { get; } = [];
    public List<BasalInjection> BasalInjections { get; } = [];
    public List<CarbIntake> CarbIntakes { get; } = [];
    public List<TempBasal> TempBasals { get; } = [];
    public List<DeviceEvent> DeviceEvents { get; } = [];
    public List<SystemEvent> SystemEvents { get; } = [];
    public List<StateSpan> StateSpans { get; } = [];
    public List<Profile> Profiles { get; } = [];
    public List<Note> Notes { get; } = [];

    /// <summary>Records the mapper could not place, for the sync log.</summary>
    public int Skipped { get; internal set; }

    public int Total => SensorGlucose.Count + BGChecks.Count + Boluses.Count + BolusCalculations.Count
                        + BasalInjections.Count + CarbIntakes.Count + TempBasals.Count
                        + DeviceEvents.Count + SystemEvents.Count + StateSpans.Count + Profiles.Count + Notes.Count;
}
