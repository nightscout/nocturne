using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Severity level for an alert rule. Critical alerts bypass quiet hours.
/// </summary>
/// <seealso cref="AlertConditionType"/>
[JsonConverter(typeof(JsonStringEnumConverter<AlertRuleSeverity>))]
public enum AlertRuleSeverity
{
    [EnumMember(Value = "critical"), JsonStringEnumMemberName("critical")]
    Critical,

    [EnumMember(Value = "warning"), JsonStringEnumMemberName("warning")]
    Warning,

    [EnumMember(Value = "info"), JsonStringEnumMemberName("info")]
    Info,
}
