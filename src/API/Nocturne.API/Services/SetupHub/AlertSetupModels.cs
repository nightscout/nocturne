using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.SetupHub;

/// <summary>Who the Alerts item sets alerts up for, from the onboarder's answer to who Nocturne is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertRouting>))]
public enum AlertRouting
{
    /// <summary>The onboarder is the patient, or has not said: alerts come to them.</summary>
    ToYou,

    /// <summary>The onboarder cares for the patient: alerts come to them, overnight included.</summary>
    ToYouAsCaregiver,

    /// <summary>The onboarder is a helper: where alerts go is left to the person they hand over to.</summary>
    LeftForRecipient,
}

/// <param name="RuleId">Null until the starter rules are first saved.</param>
/// <param name="Threshold">In <see cref="AlertSetupStatus.GlucoseUnits"/>; null for <see cref="StarterAlertKind.NoReadings"/>.</param>
public record StarterAlertRule(StarterAlertKind Kind, Guid? RuleId, bool IsEnabled, decimal? Threshold);

/// <summary>A member of the tenant other than the caller, who can also be sent urgent low alerts.</summary>
public record AlertSetupMember(Guid SubjectId, string Name, bool AlertedToUrgentLows);

/// <param name="GlucoseUnits">"mg/dl" or "mmol": the caller's units, which thresholds are read and written in.</param>
/// <param name="Saved">Whether the starter rules exist yet. Until they do, <paramref name="Rules"/> holds the starting points.</param>
/// <param name="ToThisDevice">
/// Whether alerts come to the caller's own account in Nocturne rather than to <paramref name="Channels"/>.
/// Before the rules are saved, whether that is the suggested choice.
/// </param>
/// <param name="Channels">Where alerts go when not to this device.</param>
/// <param name="ChannelTypesOffered">The channel types that can be chosen instead of this device: those that deliver while Nocturne is closed.</param>
/// <param name="DeliversWhileClosed">Whether a saved destination reaches the caller with no Nocturne page open.</param>
/// <param name="NeedsDeliveryWhileClosed">Whether the item needs such a destination to be done, as a caregiver's does.</param>
/// <param name="Verified">Whether a confirmed test alert went to the destination saved now (<see cref="AlertDeliveryCheck"/>).</param>
/// <param name="Members">The people who can also be sent urgent low alerts; only listed for <see cref="AlertRouting.ToYou"/>.</param>
public record AlertSetupStatus(
    AlertRouting Routing,
    string GlucoseUnits,
    bool Saved,
    IReadOnlyList<StarterAlertRule> Rules,
    bool ToThisDevice,
    IReadOnlyList<AlertRuleChannelResponse> Channels,
    IReadOnlyList<ChannelType> ChannelTypesOffered,
    bool DeliversWhileClosed,
    bool NeedsDeliveryWhileClosed,
    bool Verified,
    IReadOnlyList<AlertSetupMember> Members);

public record StarterAlertRuleRequest
{
    [JsonRequired]
    [EnumDataType(typeof(StarterAlertKind))]
    public StarterAlertKind Kind { get; init; }

    [JsonRequired]
    public bool IsEnabled { get; init; }

    /// <summary>In the caller's units. Required for an enabled rule of any kind but <see cref="StarterAlertKind.NoReadings"/>.</summary>
    public decimal? Threshold { get; init; }
}

public record SaveAlertSetupRequest
{
    [Required]
    public List<StarterAlertRuleRequest> Rules { get; init; } = [];

    /// <summary>Where every starter rule sends its alerts. Null sends them to the caller on this device.</summary>
    public List<CreateAlertRuleChannelRequest>? Channels { get; init; }
}

public record ConfirmSetupTestAlertRequest
{
    /// <summary>
    /// That the caller understands alerts sent only to this device show only while Nocturne is open.
    /// Required to confirm a test that reached no channel delivering while Nocturne is closed.
    /// </summary>
    public bool AcknowledgedOpenPageOnly { get; init; }
}

public record SetUrgentLowRecipientRequest
{
    [JsonRequired]
    public bool Alerted { get; init; }
}

/// <param name="Status">The delivery row's status: <c>pending</c>, <c>delivered</c> or <c>failed</c>.</param>
public record AlertSetupDelivery(ChannelType ChannelType, string Status, string? LastError);

/// <summary>A test alert sent from the Alerts item, and how far each of its deliveries got.</summary>
public record AlertSetupTest(Guid InstanceId, string RuleName, IReadOnlyList<AlertSetupDelivery> Deliveries);
