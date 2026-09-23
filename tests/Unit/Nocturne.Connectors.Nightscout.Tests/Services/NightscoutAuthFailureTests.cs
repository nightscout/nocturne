using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
///     What a failed sync tells the person whose Nightscout it is. The hand-shake is the only thing
///     that ever looks at their site, so what it learned there is all that can name the fault. A
///     site that never answered has no credential to fix, and sending that person to their API
///     secret costs them the one clue they had.
/// </summary>
public class NightscoutAuthFailureTests
{
    private const string RefusedMessage = "Nightscout refused the request.";
    private const string ServerErrorMessage = "Nightscout answered with a server error (500).";
    private const string NotNightscoutMessage = "Nothing at that address answered as Nightscout.";

    [Fact]
    public async Task BackgroundSync_WhenTheSiteCannotBeReached_SaysSoRatherThanBlamingTheSecret()
    {
        var service = NewService(new StubHandler(
            _ => throw new HttpRequestException("No such host is known", new SocketException(11001))));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(NightscoutMessages.Unreachable);
        result.Errors.Should().ContainSingle().Which.Should().Be(NightscoutMessages.Unreachable);
    }

    /// <summary>
    ///     A client timeout — the shape the dropped-packet report took — reaches the probe as a
    ///     cancellation nobody asked for.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenTheProbeTimesOut_SaysTheSiteCouldNotBeReached()
    {
        var service = NewService(new StubHandler(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout")));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Message.Should().Be(NightscoutMessages.Unreachable);
        result.Errors.Should().ContainSingle().Which.Should().Be(NightscoutMessages.Unreachable);
    }

    /// <summary>
    ///     The outbound guard already names what the person who supplied the URL has to fix. It
    ///     says more than "unreachable" can, so its wording is passed on as it stands.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenTheRequestIsRefusedBeforeItLeaves_PassesThatWordingOn()
    {
        const string refusal = "The host in your Nightscout URL could not be found.";
        var service = NewService(new StubHandler(_ => throw new OutboundRefusedException(refusal)));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Message.Should().Be(refusal);
        result.Errors.Should().ContainSingle().Which.Should().Be(refusal);
    }

    [Fact]
    public async Task RequestedSync_WhenTheSiteCannotBeReached_SaysSoOnThatEntryPointToo()
    {
        var service = NewService(new StubHandler(_ => throw new HttpRequestException("Connection refused")));

        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            NewConfig(),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(NightscoutMessages.Unreachable);
    }

    /// <summary>
    ///     A run the caller withdrew propagates the cancellation rather than arriving as a failed
    ///     credential the tenant would go looking for.
    /// </summary>
    [Fact]
    public async Task RequestedSync_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        var service = NewService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, NewConfig(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BackgroundSync_WhenTheSiteRejectsTheSecret_SaysTheSecretWasRejected()
    {
        var service = NewService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(NightscoutMessages.ApiSecretRejected);
        result.Errors.Should().ContainSingle().Which.Should().Be(NightscoutMessages.ApiSecretRejected);
        result.Message.Should().NotContain("blank", "this connector will not sync without a secret");
    }

    /// <summary>
    ///     Nightscout answers 401 for a secret it does not recognise. A 403 is as likely to be an
    ///     IP rule or a proxy in front of the site, which no change to the secret will move.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenTheSiteRefuses_NamesWhatMayBeBlockingItInstead()
    {
        var service = NewService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().StartWith(RefusedMessage);
        result.Message.Should().Contain("firewall");
        result.Message.Should().NotContain("secret");
    }

    /// <summary>
    ///     Nothing about a failing site is the tenant's to fix, so the number is kept for support
    ///     and the sentence offers the only thing they can do.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenTheSiteIsUnwell_SaysToTryAgainAndKeepsTheNumber()
    {
        var service = NewService(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().StartWith(ServerErrorMessage);
        result.Message.Should().Contain("try again");
        result.Message.Should().NotContain("secret");
    }

    /// <summary>
    ///     The probe reads a route every Nightscout has always served, so a 404 says the address
    ///     reached something that is not one.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenNothingAtTheAddressIsNightscout_SendsThemToTheAddress()
    {
        var service = NewService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().StartWith(NotNightscoutMessage);
        result.Message.Should().NotContain("secret");
    }

    /// <summary>
    ///     A manual sync reaches the connector whatever its configuration says: the loader's
    ///     incomplete-configuration gate stands in front of the scheduled poll only.
    /// </summary>
    [Fact]
    public async Task RequestedSync_WithNoSecretConfigured_AsksForTheSecret()
    {
        var service = NewService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new NightscoutConnectorConfiguration { Url = "https://ns.example", ApiSecret = "" },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(NightscoutMessages.ApiSecretMissing);
    }

    /// <summary>
    ///     A firewall answering for the site is neither of the above, and the connector already knew
    ///     to say so. That wording reaches the tenant only if a recorded reason survives the run.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenAFirewallAnswers_SaysTheFirewallIsBlockingTheSync()
    {
        var service = NewService(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "<html>challenge-platform</html>", Encoding.UTF8, "text/html"),
            };
            response.Headers.Add("cf-ray", "abc123");
            return response;
        }));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Message.Should().Contain("firewall");
        result.Message.Should().NotContain("API secret");
    }

    /// <summary>
    ///     A failure nobody anticipated is the log's business: its text names an internal state,
    ///     not anything the tenant can act on.
    /// </summary>
    [Fact]
    public async Task BackgroundSync_WhenTheProbeFailsUnexpectedly_KeepsTheExceptionOutOfWhatTheTenantReads()
    {
        var service = NewService(new StubHandler(
            _ => throw new InvalidOperationException("Collection was modified")));

        var result = await service.SyncDataAsync(NewConfig(), CancellationToken.None);

        result.Message.Should().Be(NightscoutMessages.CheckFailed);
        result.Errors.Should().ContainSingle().Which.Should().NotContain("Collection was modified");
    }

    private static NightscoutConnectorConfiguration NewConfig() => new()
    {
        Url = "https://ns.example",
        ApiSecret = "secret",
    };

    private static NightscoutConnectorService NewService(StubHandler handler)
    {
        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

        return new NightscoutConnectorService(
            new HttpClient(handler),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            registration.Object,
            Mock.Of<IConnectorPublisher>());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(answer(request));
        }
    }
}
