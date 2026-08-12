using FluentAssertions;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Ties the channel types the rule editor offers to the delivery switch in
/// <c>AlertDeliveryService.DispatchToProviderAsync</c>. An offered type no provider claims is a
/// channel a user can configure, save, and never hear from.
/// </summary>
[Trait("Category", "Unit")]
public class ChannelDestinationsGuardTests
{
    /// <summary>
    /// The non-chat providers <c>DispatchToProviderAsync</c> switches on by name, restated here so
    /// the switch and the offered list have to agree.
    /// </summary>
    private static readonly ChannelType[] DirectlyProvidedTypes =
    [
        ChannelType.WebPush,
        ChannelType.InApp,
        ChannelType.Webhook,
        ChannelType.HomeAssistant,
        ChannelType.DeviceAction,
    ];

    [Fact]
    public void Every_offered_channel_type_reaches_a_provider()
    {
        var offered = ChannelDestinations.Offered;

        offered.Should().NotBeEmpty("an empty offered list would pass every other assertion here");
        offered.Should().OnlyContain(
            t => DirectlyProvidedTypes.Contains(t) || ChatBotProvider.SupportedChannelTypes.Contains(t));
    }

    [Fact]
    public void Every_offered_channel_type_is_destination_validated_or_identity_resolved()
    {
        var offered = ChannelDestinations.Offered;

        offered.Should().NotBeEmpty("an empty offered list would pass every other assertion here");

        foreach (var channelType in offered)
        {
            var mode = ChannelDestinations.ModeOf(channelType);
            mode.Should().NotBeNull($"{channelType} is offered without a destination mode");

            if (mode == ChannelDestinationMode.LinkedIdentity)
            {
                ChannelDestinations.PlatformOf(channelType).Should().NotBeNull(
                    $"{channelType} is resolved from a linked identity, which needs a platform to look up");
            }
        }
    }

    [Fact]
    public void Chat_platforms_are_assigned_to_exactly_the_types_the_bot_delivers()
    {
        var withPlatform = Enum.GetValues<ChannelType>()
            .Where(t => ChannelDestinations.PlatformOf(t) is not null);

        withPlatform.Should().BeEquivalentTo(ChatBotProvider.SupportedChannelTypes);
    }

    [Fact]
    public void Superseded_channel_types_are_never_offered()
    {
        var superseded = Enum.GetValues<ChannelType>()
            .Where(t => ChannelDestinations.SupersededBy(t) is not null)
            .ToList();

        superseded.Should().NotBeEmpty();
        superseded.Should().OnlyContain(t => !ChannelDestinations.Offered.Contains(t));
        superseded.Should().OnlyContain(t => !ChatBotProvider.SupportedChannelTypes.Contains(t));
    }
}
