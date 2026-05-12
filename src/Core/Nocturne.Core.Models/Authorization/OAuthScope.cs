using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Backend-owned OAuth scope taxonomy exposed to generated frontend clients.
/// Values intentionally match the RFC 6749 scope strings used on the wire.
/// </summary>
/// <seealso cref="OAuthScopes"/>
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

    [EnumMember(Value = "statistics.read"), JsonStringEnumMemberName("statistics.read")]
    StatisticsRead,

    [EnumMember(Value = "health.read"), JsonStringEnumMemberName("health.read")]
    HealthRead,

    [EnumMember(Value = "health.readwrite"), JsonStringEnumMemberName("health.readwrite")]
    HealthReadWrite,

    [EnumMember(Value = "*"), JsonStringEnumMemberName("*")]
    FullAccess
}
