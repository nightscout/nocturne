using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The requestable scope vocabulary, as an enum, so the generated TypeScript client has a typed
/// handle on it. <see cref="Scope"/> is the source of truth; this restates it for the wire only.
/// Values are the RFC 6749 scope strings. <c>OAuthScopeEnumConformanceTests</c> pins the two
/// together — a member added to one and not the other fails the build, which is how
/// <c>device.notify</c> and <c>device.actuate</c> previously went missing from here.
/// </summary>
/// <seealso cref="Scope"/>
/// <seealso cref="ScopeTranslator"/>
[JsonConverter(typeof(JsonStringEnumConverter<OAuthScope>))]
public enum OAuthScope
{
    [EnumMember(Value = "glucose.read"), JsonStringEnumMemberName("glucose.read")]
    GlucoseRead,

    [EnumMember(Value = "glucose.readwrite"), JsonStringEnumMemberName("glucose.readwrite")]
    GlucoseReadWrite,

    [EnumMember(Value = "treatments.read"), JsonStringEnumMemberName("treatments.read")]
    TreatmentsRead,

    [EnumMember(Value = "treatments.readwrite"), JsonStringEnumMemberName("treatments.readwrite")]
    TreatmentsReadWrite,

    [EnumMember(Value = "devices.read"), JsonStringEnumMemberName("devices.read")]
    DevicesRead,

    [EnumMember(Value = "devices.readwrite"), JsonStringEnumMemberName("devices.readwrite")]
    DevicesReadWrite,

    [EnumMember(Value = "therapy.read"), JsonStringEnumMemberName("therapy.read")]
    TherapyRead,

    [EnumMember(Value = "therapy.readwrite"), JsonStringEnumMemberName("therapy.readwrite")]
    TherapyReadWrite,

    [EnumMember(Value = "alerts.read"), JsonStringEnumMemberName("alerts.read")]
    AlertsRead,

    [EnumMember(Value = "alerts.readwrite"), JsonStringEnumMemberName("alerts.readwrite")]
    AlertsReadWrite,

    [EnumMember(Value = "reports.read"), JsonStringEnumMemberName("reports.read")]
    ReportsRead,

    [EnumMember(Value = "identity.read"), JsonStringEnumMemberName("identity.read")]
    IdentityRead,

    [EnumMember(Value = "sharing.readwrite"), JsonStringEnumMemberName("sharing.readwrite")]
    SharingReadWrite,

    [EnumMember(Value = "heartrate.read"), JsonStringEnumMemberName("heartrate.read")]
    HeartRateRead,

    [EnumMember(Value = "heartrate.readwrite"), JsonStringEnumMemberName("heartrate.readwrite")]
    HeartRateReadWrite,

    [EnumMember(Value = "stepcount.read"), JsonStringEnumMemberName("stepcount.read")]
    StepCountRead,

    [EnumMember(Value = "stepcount.readwrite"), JsonStringEnumMemberName("stepcount.readwrite")]
    StepCountReadWrite,

    [EnumMember(Value = "sleep.read"), JsonStringEnumMemberName("sleep.read")]
    SleepRead,

    [EnumMember(Value = "sleep.readwrite"), JsonStringEnumMemberName("sleep.readwrite")]
    SleepReadWrite,

    [EnumMember(Value = "food.read"), JsonStringEnumMemberName("food.read")]
    FoodRead,

    [EnumMember(Value = "food.readwrite"), JsonStringEnumMemberName("food.readwrite")]
    FoodReadWrite,

    [EnumMember(Value = "device.notify"), JsonStringEnumMemberName("device.notify")]
    DeviceNotify,

    [EnumMember(Value = "device.actuate"), JsonStringEnumMemberName("device.actuate")]
    DeviceActuate,

    [EnumMember(Value = "health.read"), JsonStringEnumMemberName("health.read")]
    HealthRead,

    [EnumMember(Value = "health.readwrite"), JsonStringEnumMemberName("health.readwrite")]
    HealthReadWrite,

    [EnumMember(Value = "*"), JsonStringEnumMemberName("*")]
    FullAccess
}
