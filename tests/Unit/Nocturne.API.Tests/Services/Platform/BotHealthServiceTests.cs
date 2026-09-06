using FluentAssertions;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Tests.Services.Platform;

[Trait("Category", "Unit")]
public class BotHealthServiceTests
{
    private readonly BotHealthService _sut = new();

    [Fact]
    public void GetChannelStatuses_NoHeartbeatReceived_BotChannelsAreUnavailable()
    {
        var statuses = _sut.GetChannelStatuses();
        var discord = statuses.First(s => s.ChannelType == ChannelType.DiscordDm);
        discord.Status.Should().Be(ChannelStatus.Unavailable);
        discord.Reason.Should().Be(ChannelUnavailableReason.AdapterNotConfigured);
    }

    [Fact]
    public void GetChannelStatuses_NoHeartbeatReceived_WebPushAlwaysAvailable()
    {
        var statuses = _sut.GetChannelStatuses();
        var webPush = statuses.First(s => s.ChannelType == ChannelType.WebPush);
        webPush.Status.Should().Be(ChannelStatus.Available);
        webPush.Reason.Should().BeNull();
    }

    [Fact]
    public void GetChannelStatuses_NoHeartbeatReceived_WebhookAlwaysAvailable()
    {
        var statuses = _sut.GetChannelStatuses();
        var webhook = statuses.First(s => s.ChannelType == ChannelType.Webhook);
        webhook.Status.Should().Be(ChannelStatus.Available);
        webhook.Reason.Should().BeNull();
    }

    [Fact]
    public void GetChannelStatuses_AfterHeartbeat_ReportedPlatformsAreAvailable()
    {
        _sut.Record(["discord", "telegram"]);
        var statuses = _sut.GetChannelStatuses();
        statuses.First(s => s.ChannelType == ChannelType.DiscordDm).Status.Should().Be(ChannelStatus.Available);
        statuses.First(s => s.ChannelType == ChannelType.TelegramGroup).Status.Should().Be(ChannelStatus.Available);
    }

    [Fact]
    public void GetChannelStatuses_AfterHeartbeat_EveryChannelOnAPlatformIsReported()
    {
        _sut.Record(["slack"]);
        var statuses = _sut.GetChannelStatuses();
        statuses.First(s => s.ChannelType == ChannelType.SlackDm).Status.Should().Be(ChannelStatus.Available);
        statuses.First(s => s.ChannelType == ChannelType.SlackChannel).Status.Should().Be(ChannelStatus.Available);
    }

    [Fact]
    public void GetChannelStatuses_CarriesTheDestinationRulesForTheEditor()
    {
        var statuses = _sut.GetChannelStatuses();

        var slackChannel = statuses.First(s => s.ChannelType == ChannelType.SlackChannel);
        slackChannel.Offered.Should().BeTrue();
        slackChannel.RequiresDestination.Should().BeTrue();

        var slackDm = statuses.First(s => s.ChannelType == ChannelType.SlackDm);
        slackDm.Offered.Should().BeTrue();
        slackDm.RequiresDestination.Should().BeFalse();
        slackDm.RequiresLink.Should().BeTrue();

        statuses.First(s => s.ChannelType == ChannelType.Telegram).Offered.Should().BeFalse();
        statuses.First(s => s.ChannelType == ChannelType.WhatsApp).Offered.Should().BeFalse();
    }

    [Fact]
    public void GetChannelStatuses_AfterHeartbeat_UnreportedPlatformsAreUnavailable()
    {
        _sut.Record(["discord"]);
        var statuses = _sut.GetChannelStatuses();
        statuses.First(s => s.ChannelType == ChannelType.SlackDm).Status.Should().Be(ChannelStatus.Unavailable);
    }

    [Fact]
    public void GetChannelStatuses_StaleHeartbeat_ReportedPlatformsAreDegraded()
    {
        _sut.Record(["discord"], DateTime.UtcNow.AddMinutes(-3));
        var statuses = _sut.GetChannelStatuses();
        var discord = statuses.First(s => s.ChannelType == ChannelType.DiscordDm);
        discord.Status.Should().Be(ChannelStatus.Degraded);
        discord.Reason.Should().Be(ChannelUnavailableReason.HeartbeatStale);
    }

    [Fact]
    public void GetChannelStatuses_ReturnsAllChannelTypes()
    {
        var statuses = _sut.GetChannelStatuses();
        statuses.Should().HaveCount(Enum.GetValues<ChannelType>().Length);
    }

    [Fact]
    public void GetChannelStatuses_BotChannelsRequireLink()
    {
        _sut.Record(["discord"]);
        var statuses = _sut.GetChannelStatuses();
        statuses.First(s => s.ChannelType == ChannelType.DiscordDm).RequiresLink.Should().BeTrue();
        statuses.First(s => s.ChannelType == ChannelType.WebPush).RequiresLink.Should().BeFalse();
    }
}
