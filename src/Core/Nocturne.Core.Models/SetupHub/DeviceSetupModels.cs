using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.SetupHub;

/// <summary>What a setup hub item created on the owner's behalf.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SetupHubRecordKind>))]
public enum SetupHubRecordKind
{
    Insulin,
    Tracker,
}

/// <summary>Where a device guess came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceEvidenceSource>))]
public enum DeviceEvidenceSource
{
    /// <summary>An enabled connector the tenant set up.</summary>
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

/// <summary>
/// The AID algorithm the evidence points at. It is only a suggestion: the owner confirms or
/// changes it with the pump, and nothing records it otherwise.
/// </summary>
public record AlgorithmGuess(AidAlgorithm Algorithm, IReadOnlyList<DeviceEvidence> Evidence);

[JsonConverter(typeof(JsonStringEnumConverter<InsulinGroup>))]
public enum InsulinGroup
{
    RapidActing,
    LongActing,
}

/// <param name="RecordedId">The current patient insulin of this formulation, if there is one.</param>
/// <param name="AddedHere">
/// Whether the Devices item recorded it, and so may take it back. One that was on record before
/// is shown as picked but cannot be unpicked here.
/// </param>
public record InsulinChoice(InsulinFormulation Formulation, Guid? RecordedId, bool AddedHere);

public record InsulinChoiceGroup(InsulinGroup Group, IReadOnlyList<InsulinChoice> Choices);

[JsonConverter(typeof(JsonStringEnumConverter<TrackerOfferKind>))]
public enum TrackerOfferKind
{
    Sensor,
    Pod,
    InfusionSet,
    Reservoir,
}

[JsonConverter(typeof(JsonStringEnumConverter<TrackerOfferState>))]
public enum TrackerOfferState
{
    Off,

    /// <summary>The Devices item created the tracker, so it can turn it off again.</summary>
    AddedHere,

    /// <summary>
    /// The owner already had a tracker for this; it is left alone and managed in tracker settings.
    /// </summary>
    AlreadyTracked,
}

/// <param name="DeviceName">The catalogue name of the recorded device the tracker follows.</param>
/// <param name="WearDays">
/// Whole days of the catalogue's typical wear time; with <paramref name="WearHours"/> it makes the
/// full time. Both null when the catalogue rates none.
/// </param>
/// <param name="WearHours">Hours past <paramref name="WearDays"/>.</param>
/// <param name="DefinitionId">The tracker behind <paramref name="State"/>, when it is not off.</param>
public record TrackerOffer(
    TrackerOfferKind Kind,
    string DeviceName,
    int? WearDays,
    int? WearHours,
    TrackerOfferState State,
    Guid? DefinitionId);

/// <summary>Everything the Devices setup item shows.</summary>
/// <param name="Devices">The CGM slot, then the pump slot.</param>
/// <param name="Algorithm">The AID algorithm the evidence points at, for the owner to confirm with the pump.</param>
/// <param name="Insulins">The patient's current insulins.</param>
/// <param name="OtherInsulins">Current insulins that are not on the short list.</param>
/// <param name="ActionTime">
/// The insulin action time Nocturne uses now for insulin on board and predictions, and its source.
/// </param>
/// <param name="TakesNoInsulin">The owner answered that no insulin is used, and none is on record.</param>
/// <param name="Trackers">Trackers offered for the recorded devices.</param>
public record DeviceSetup(
    IReadOnlyList<DeviceSlot> Devices,
    AlgorithmGuess? Algorithm,
    IReadOnlyList<InsulinChoiceGroup> InsulinChoices,
    IReadOnlyList<PatientInsulin> Insulins,
    IReadOnlyList<PatientInsulin> OtherInsulins,
    InsulinActionTime ActionTime,
    bool TakesNoInsulin,
    IReadOnlyList<TrackerOffer> Trackers);
