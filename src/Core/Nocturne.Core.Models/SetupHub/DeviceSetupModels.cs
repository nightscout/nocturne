using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.SetupHub;

/// <summary>Where a device guess came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceEvidenceSource>))]
public enum DeviceEvidenceSource
{
    /// <summary>A connector the tenant set up.</summary>
    Connector,

    /// <summary>The pump status an uploader or connector reports.</summary>
    PumpStatus,

    /// <summary>The loop status an AID app (Loop, AAPS, Trio, OpenAPS) uploads.</summary>
    AlgorithmStatus,

    /// <summary>The device label on recent glucose readings (e.g. xDrip+ or AAPS naming the sensor).</summary>
    Readings,
}

/// <param name="Detail">
/// What the source said, verbatim: a connector name, a pump's reported manufacturer and model, an
/// algorithm, or a reading's device label.
/// </param>
public record DeviceEvidence(DeviceEvidenceSource Source, string Detail);

/// <summary>One kind of device on the guided page: the CGM, or the insulin pump.</summary>
/// <param name="Recorded">
/// The current device of this category the patient already has on record. Once there is one,
/// nothing is guessed for the category.
/// </param>
/// <param name="Guess">The catalogue entry the evidence points at; null when nothing does.</param>
/// <param name="ModelKnown">
/// False when the evidence names only the manufacturer, so <paramref name="Guess"/> is that
/// manufacturer's first model in the catalogue and needs checking.
/// </param>
/// <param name="Choices">The catalogue entries of this category the guess can be swapped for.</param>
public record DeviceSlot(
    DeviceCategory Category,
    PatientDevice? Recorded,
    DeviceCatalogEntry? Guess,
    bool ModelKnown,
    IReadOnlyList<DeviceEvidence> Evidence,
    IReadOnlyList<DeviceCatalogEntry> Choices);

public record AlgorithmGuess(AidAlgorithm Algorithm, IReadOnlyList<DeviceEvidence> Evidence);

[JsonConverter(typeof(JsonStringEnumConverter<InsulinGroup>))]
public enum InsulinGroup
{
    RapidActing,
    LongActing,
}

public record InsulinChoiceGroup(InsulinGroup Group, IReadOnlyList<InsulinFormulation> Formulations);

[JsonConverter(typeof(JsonStringEnumConverter<TrackerOfferKind>))]
public enum TrackerOfferKind
{
    Sensor,
    Pod,
    InfusionSet,
    Reservoir,
}

/// <param name="DeviceName">The catalogue name of the recorded device the tracker follows.</param>
/// <param name="LifespanHours">
/// The manufacturer's rated wear time from the catalogue; null when the catalogue rates none.
/// </param>
/// <param name="DefinitionId">The caller's tracker that already covers this, if there is one.</param>
public record TrackerOffer(TrackerOfferKind Kind, string DeviceName, int? LifespanHours, Guid? DefinitionId);

/// <summary>Everything the Devices setup item shows.</summary>
/// <param name="Devices">The CGM slot, then the pump slot.</param>
/// <param name="Algorithm">The AID algorithm the evidence points at, recorded on the pump when it is confirmed.</param>
/// <param name="Insulins">The patient's current insulins.</param>
/// <param name="TakesNoInsulin">The owner answered that no insulin is used, and none is on record.</param>
/// <param name="Trackers">Trackers offered for the recorded devices.</param>
public record DeviceSetup(
    IReadOnlyList<DeviceSlot> Devices,
    AlgorithmGuess? Algorithm,
    IReadOnlyList<InsulinChoiceGroup> InsulinChoices,
    IReadOnlyList<PatientInsulin> Insulins,
    bool TakesNoInsulin,
    IReadOnlyList<TrackerOffer> Trackers);
