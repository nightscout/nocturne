using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>What a part of an insulin pump does for the wearer, and so what renewing it renews.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PumpCapability>))]
public enum PumpCapability
{
    /// <summary>The cannula and the adhesive that hold it in the body.</summary>
    Site,

    /// <summary>The insulin.</summary>
    Reservoir,

    /// <summary>The battery that runs the pump.</summary>
    Power,
}

/// <summary>The kind of part a <see cref="PumpComponent"/> is, which names and files its tracker.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PumpComponentKind>))]
public enum PumpComponentKind
{
    /// <summary>
    /// A disposable worn on the body that is the site and the reservoir together: an Omnipod pod, a
    /// Medtrum patch, a Mint cartridge.
    /// </summary>
    Pod,

    /// <summary>A cannula and its adhesive, without the insulin.</summary>
    InfusionSet,

    /// <summary>A reservoir or cartridge, without the site.</summary>
    Reservoir,

    /// <summary>A reusable pump body the disposable parts attach to.</summary>
    Base,

    /// <summary>A reusable part that holds the pump's electronics and is not worn on its own.</summary>
    Controller,
}

/// <summary>Whether a <see cref="PumpComponent"/> is thrown away when it runs out.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComponentLifecycle>))]
public enum ComponentLifecycle
{
    /// <summary>Replaced after a fixed wear time.</summary>
    Disposable,

    /// <summary>Kept across many disposables, and replaced after a service life if it has one.</summary>
    Durable,
}

/// <summary>How a <see cref="PumpComponent"/> that provides <see cref="PumpCapability.Power"/> gets it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComponentPower>))]
public enum ComponentPower
{
    /// <summary>A battery sealed in the part, used up with it.</summary>
    Embedded,

    /// <summary>A battery the wearer swaps.</summary>
    Replaceable,

    /// <summary>A battery the wearer charges.</summary>
    Rechargeable,
}

/// <summary>
/// One part of an insulin pump that wears out or is kept, from a <see cref="PumpProperties"/>. A
/// tracker follows each part that has a lifespan, and the parts' <see cref="Provides"/> say which
/// legacy ages and alerts that tracker answers for.
/// </summary>
public record PumpComponent
{
    /// <summary>Kebab-case identifier, unique within its pump (e.g. "pod", "base").</summary>
    public required string Key { get; init; }

    public required PumpComponentKind Kind { get; init; }

    /// <summary>What replacing the part renews.</summary>
    public required IReadOnlyList<PumpCapability> Provides { get; init; }

    public required ComponentLifecycle Lifecycle { get; init; }

    /// <summary>
    /// Approved wear of a disposable, or the service life of a durable part, in hours. Null for a
    /// durable part the manufacturer gives no service life.
    /// </summary>
    public int? LifespanHours { get; init; }

    /// <summary>Hours past <see cref="LifespanHours"/> the part keeps working before it stops.</summary>
    public int? GraceHours { get; init; }

    /// <summary>How the part is powered, when it provides <see cref="PumpCapability.Power"/>.</summary>
    public ComponentPower? Power { get; init; }

    /// <summary>The device events that mean the part was replaced.</summary>
    public IReadOnlyList<DeviceEventType> ResetBy { get; init; } = [];

    /// <summary>The device events that mean the part was taken off and nothing replaced it yet.</summary>
    public IReadOnlyList<DeviceEventType> EndedBy { get; init; } = [];

    /// <summary>
    /// Whether registering the pump gives the part a tracker. False for a part whose service life
    /// varies too much to remind on unasked.
    /// </summary>
    public bool TrackedByDefault { get; init; } = true;
}
