using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Pins that the composition root owns the clock. Services across alerting, auth and the
/// processing-status cache take <see cref="TimeProvider"/> by constructor; before this
/// registration existed they resolved one only because <c>AddAuthentication</c> happens to
/// <c>TryAdd</c> the same instance, so moving that call would have broken them at runtime with
/// nothing in the tree to point at.
/// </summary>
/// <remarks>
/// Asserted against <see cref="ServiceRegistrationExtensions.AddApiCoreServices"/> rather than a
/// built host: the host resolves <see cref="TimeProvider.System"/> either way, so a host-level
/// check cannot fail if this registration is removed.
/// </remarks>
public class HostClockRegistrationTests
{
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
