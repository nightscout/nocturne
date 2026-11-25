using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Constants;
using Xunit;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
///     A Nightscout can be readable without an API secret: open to read, a default role that reads,
///     or a proxy in front of it that admits requests under a secret path. The import has always
///     read such a site, so the connector reads it too, and asks for a secret only when Nightscout
///     itself refuses.
/// </summary>
public class NightscoutWithoutSecretTests
{
    private const string Prefix = "/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string PrefixedUrl = "https://nscout.example.com" + Prefix + "/";

    private static readonly SyncDataType[] EveryType =
    [
        SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.ManualBG,
        SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents,
        SyncDataType.Profiles, SyncDataType.DeviceStatus, SyncDataType.Food, SyncDataType.Activity,
    ];

    [Fact]
    public void A_configuration_with_no_secret_is_complete()
    {
        var config = new NightscoutConnectorConfiguration { Url = PrefixedUrl };

        config.MissingRequiredProperties().Should().BeEmpty();
        config.HasRequiredConfiguration().Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_sync_with_no_secret_reads_and_sends_no_secret_header(string apiSecret)
    {
        var handler = AnswersEveryReadWithNothing();
        var service = NewService(handler);

        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [.. EveryType] },
            new NightscoutConnectorConfiguration { Url = PrefixedUrl, ApiSecret = apiSecret },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Message);
        handler.RequestCount.Should().BeGreaterThan(1);
        handler.CarriedApiSecret.Should().AllSatisfy(carried => carried.Should().BeFalse(
            "a hash of an empty secret is a wrong secret, not an absent one"));
    }

    [Fact]
    public async Task A_sync_with_a_secret_sends_its_hash_on_every_read()
    {
        var handler = AnswersEveryReadWithNothing();
        var service = NewService(handler);

        await service.SyncDataAsync(
            new SyncRequest { DataTypes = [.. EveryType] },
            new NightscoutConnectorConfiguration { Url = PrefixedUrl, ApiSecret = "secret" },
            CancellationToken.None);

        handler.ApiSecretHeaders.Should().NotBeEmpty()
            .And.AllBe(NightscoutConnectorService.ComputeApiSecretHash("secret"));
    }

    [Fact]
    public async Task Every_read_keeps_the_sites_path_and_query()
    {
        var handler = AnswersEveryReadWithNothing();
        var service = NewService(handler);

        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [.. EveryType] },
            new NightscoutConnectorConfiguration { Url = PrefixedUrl + "?token=reader-abc123" },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Message);
        handler.Uris.Should().AllSatisfy(uri =>
        {
            uri.Host.Should().Be("nscout.example.com");
            uri.AbsolutePath.Should().StartWith(Prefix + "/api/v1/");
            uri.Query.Should().Contain("token=reader-abc123");
        });
        handler.Uris.Select(u => u.AbsolutePath[(Prefix.Length + "/api/v1/".Length)..]).Distinct()
            .Should().Contain(["entries.json", "treatments.json", "profile.json", "devicestatus.json", "food.json", "activity.json"]);
    }

    [Fact]
    public async Task A_refusal_of_a_read_with_no_secret_asks_for_one()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Unauthorized);
        var service = NewService(handler);

        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new NightscoutConnectorConfiguration { Url = PrefixedUrl },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(NightscoutMessages.ApiSecretMissing);
        handler.CarriedApiSecret.Should().ContainSingle().Which.Should().BeFalse();
    }

    private static RecordingHttpMessageHandler AnswersEveryReadWithNothing() => new()
    {
        Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        },
    };

    private static NightscoutConnectorService NewService(HttpMessageHandler handler)
    {
        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

        return new NightscoutConnectorService(
            new HttpClient(handler),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            registration.Object,
            new NightscoutLookups(),
            MockTenantAccessor.Create().Object,
            publisher: null);
    }
}
