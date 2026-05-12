using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Typed permission atoms for the tenant RBAC system.
/// Values match the wire strings used in <see cref="TenantPermissions"/>.
/// </summary>
/// <seealso cref="TenantPermissions"/>
/// <seealso cref="OAuthScope"/>
[JsonConverter(typeof(JsonStringEnumConverter<TenantPermission>))]
public enum TenantPermission
{
    // Patient Record

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

    [EnumMember(Value = "food.read"), JsonStringEnumMemberName("food.read")]
    FoodRead,

    [EnumMember(Value = "food.readwrite"), JsonStringEnumMemberName("food.readwrite")]
    FoodReadWrite,

    [EnumMember(Value = "statistics.read"), JsonStringEnumMemberName("statistics.read")]
    StatisticsRead,

    [EnumMember(Value = "reports.read"), JsonStringEnumMemberName("reports.read")]
    ReportsRead,

    // Therapy Settings

    [EnumMember(Value = "therapy.read"), JsonStringEnumMemberName("therapy.read")]
    TherapyRead,

    [EnumMember(Value = "therapy.readwrite"), JsonStringEnumMemberName("therapy.readwrite")]
    TherapyReadWrite,

    [EnumMember(Value = "alerts.read"), JsonStringEnumMemberName("alerts.read")]
    AlertsRead,

    [EnumMember(Value = "alerts.readwrite"), JsonStringEnumMemberName("alerts.readwrite")]
    AlertsReadWrite,

    // Account

    [EnumMember(Value = "identity.read"), JsonStringEnumMemberName("identity.read")]
    IdentityRead,

    // Administration

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

    // Audit

    [EnumMember(Value = "audit.read"), JsonStringEnumMemberName("audit.read")]
    AuditRead,

    [EnumMember(Value = "audit.manage"), JsonStringEnumMemberName("audit.manage")]
    AuditManage,

    // Superuser

    [EnumMember(Value = "*"), JsonStringEnumMemberName("*")]
    Superuser,
}
