using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// What an excursion acknowledgement did, decided by the caller's authority.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertAcknowledgementOutcome>))]
public enum AlertAcknowledgementOutcome
{
    /// <summary>The excursion is acknowledged for everyone: escalation stops for every recipient.</summary>
    [EnumMember(Value = "acknowledged"), JsonStringEnumMemberName("acknowledged")]
    Acknowledged,

    /// <summary>The excursion is muted for the caller only: everyone else's escalation continues.</summary>
    [EnumMember(Value = "muted"), JsonStringEnumMemberName("muted")]
    Muted,

    /// <summary>The excursion has already closed, so there was nothing to acknowledge.</summary>
    [EnumMember(Value = "closed"), JsonStringEnumMemberName("closed")]
    Closed,
}
