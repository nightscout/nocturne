using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The atoms a tenant role, invite or direct member permission may confer, as an enum, so the
/// generated TypeScript client has a typed handle on them. <see cref="Scope.PermissionAtoms"/> is
/// the source of truth; <c>PermissionAtomEnumConformanceTests</c> pins the two together.
/// </summary>
/// <seealso cref="Scope"/>
/// <seealso cref="OAuthScope"/>
[JsonConverter(typeof(JsonStringEnumConverter<PermissionAtom>))]
public enum PermissionAtom
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

    [EnumMember(Value = "reports.read"), JsonStringEnumMemberName("reports.read")]
    ReportsRead,

    [EnumMember(Value = "therapy.read"), JsonStringEnumMemberName("therapy.read")]
    TherapyRead,

    [EnumMember(Value = "therapy.readwrite"), JsonStringEnumMemberName("therapy.readwrite")]
    TherapyReadWrite,

    [EnumMember(Value = "alerts.read"), JsonStringEnumMemberName("alerts.read")]
    AlertsRead,

    [EnumMember(Value = "alerts.readwrite"), JsonStringEnumMemberName("alerts.readwrite")]
    AlertsReadWrite,

    [EnumMember(Value = "alerts.acknowledge"), JsonStringEnumMemberName("alerts.acknowledge")]
    AlertsAcknowledge,

    [EnumMember(Value = "identity.read"), JsonStringEnumMemberName("identity.read")]
    IdentityRead,

    [EnumMember(Value = "roles.manage"), JsonStringEnumMemberName("roles.manage")]
    RolesManage,

    [EnumMember(Value = "members.invite"), JsonStringEnumMemberName("members.invite")]
    MembersInvite,

    [EnumMember(Value = "members.manage"), JsonStringEnumMemberName("members.manage")]
    MembersManage,

    [EnumMember(Value = "tenant.settings"), JsonStringEnumMemberName("tenant.settings")]
    TenantSettings,

    [EnumMember(Value = "sharing.manage"), JsonStringEnumMemberName("sharing.manage")]
    SharingManage,

    [EnumMember(Value = "sharing.guest"), JsonStringEnumMemberName("sharing.guest")]
    SharingGuest,

    [EnumMember(Value = "audit.read"), JsonStringEnumMemberName("audit.read")]
    AuditRead,

    [EnumMember(Value = "audit.manage"), JsonStringEnumMemberName("audit.manage")]
    AuditManage,

    [EnumMember(Value = "device.notify"), JsonStringEnumMemberName("device.notify")]
    DeviceNotify,

    [EnumMember(Value = "device.actuate"), JsonStringEnumMemberName("device.actuate")]
    DeviceActuate
}
