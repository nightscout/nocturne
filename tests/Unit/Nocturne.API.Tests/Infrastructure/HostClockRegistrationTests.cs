using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Pins that the composition root owns the clock. Services across alerting, auth and the
/// processing-status cache take <see cref="TimeProvider"/> by constructor; before this
/// registration existed they resolved one only because some framework extension the host
/// called registered it in passing, so dropping that call would have broken them at runtime
/// with nothing in the tree to point at.
/// </summary>
public class HostClockRegistrationTests : IClassFixture<AuthenticationTestFactory>
{
    private readonly AuthenticationTestFactory _factory;

    public HostClockRegistrationTests(AuthenticationTestFactory factory) => _factory = factory;

    [Fact]
    [Trait("Category", "Unit")]
    public void BuiltHost_Resolves_TheSystemClock()
    {
        _factory.Services.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    /// <summary>
    /// Asserted against <see cref="ServiceRegistrationExtensions.AddApiCoreServices"/> on its own,
    /// because the host-level check above passes on the incidental registration too and so cannot
    /// fail if this one is removed.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ApiCoreServices_Registers_TheSystemClock()
    {
        var services = new ServiceCollection();
        services.AddApiCoreServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
