using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>How a person takes insulin, as the devices and insulins on their record say.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TherapyType>))]
public enum TherapyType
{
    /// <summary>The owner answered that no insulin is used, and none is on record.</summary>
    NoInsulin,

    /// <summary>A current insulin pump run by an automated delivery algorithm.</summary>
    AutomatedDelivery,

    /// <summary>A current insulin pump with no automated delivery algorithm.</summary>
    Pump,

    /// <summary>A current insulin on record, and no current pump.</summary>
    Injections,
}

/// <summary>A person's <see cref="TherapyType"/> from what their record holds.</summary>
public static class TherapyTypes
{
    /// <summary>The first insulin pump of <paramref name="devices"/>, in their order; null when there is none.</summary>
    public static PatientDevice? PumpOf(IEnumerable<PatientDevice> devices) =>
        devices.FirstOrDefault(d => d.DeviceCategory == DeviceCategory.InsulinPump);

    /// <summary>
    /// The therapy of a person whose current devices are <paramref name="currentDevices"/>; null when nothing on the
    /// record says. A pump outweighs the rest, and a current insulin outweighs <paramref name="takesNoInsulin"/>, as
    /// the setup hub reads it. An algorithm of <see cref="AidAlgorithm.None"/> or <see cref="AidAlgorithm.Unknown"/>
    /// is no algorithm.
    /// </summary>
    public static TherapyType? Of(
        IEnumerable<PatientDevice> currentDevices, bool hasCurrentInsulin, bool takesNoInsulin) =>
        PumpOf(currentDevices) is { } pump
            ? pump.AidAlgorithm is { } algorithm && algorithm is not (AidAlgorithm.None or AidAlgorithm.Unknown)
                ? TherapyType.AutomatedDelivery
                : TherapyType.Pump
            : hasCurrentInsulin ? TherapyType.Injections
            : takesNoInsulin ? TherapyType.NoInsulin
            : null;
}

/// <summary>What a person's record says of them, as a People query reads it.</summary>
/// <param name="CurrentDevices">Null when the therapy was not read.</param>
public sealed record PatientFacts(
    DateOnly? DateOfBirth, bool TakesNoInsulin, IReadOnlyList<PatientDevice>? CurrentDevices, bool HasCurrentInsulin)
{
    /// <summary>The <see cref="TherapyTypes.Of"/> of these facts; null when nothing says, or the therapy was not read.</summary>
    public TherapyType? Therapy =>
        CurrentDevices is { } devices ? TherapyTypes.Of(devices, HasCurrentInsulin, TakesNoInsulin) : null;
}
