using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.MyLife.Configurations;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

public class MyLifeTokenExpiryTests
{
    [Fact]
    public async Task Token_ExpiresAnHourBeforeItsTwentyFourHourLifetimeEnds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (_, _, tokenProvider) = MyLifeSyncHarness.BuildService(
            new HttpClient(new SoapStubHandler(loginSucceeds: true)),
            Guid.NewGuid(),
            clock: clock);

        var token = await tokenProvider.GetValidTokenAsync(
            new MyLifeConnectorConfiguration
            {
                Username = "user@example.com",
                Password = "secret",
                PatientId = "patient-1",
            },
            CancellationToken.None);

        token.Should().NotBeNullOrEmpty();
        tokenProvider.TokenExpiresAt.Should().Be(clock.GetUtcNow().UtcDateTime.AddHours(23));
        clock.Advance(TimeSpan.FromHours(23) - TimeSpan.FromSeconds(1));
        tokenProvider.IsTokenExpired.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        tokenProvider.IsTokenExpired.Should().BeTrue();
    }
}
