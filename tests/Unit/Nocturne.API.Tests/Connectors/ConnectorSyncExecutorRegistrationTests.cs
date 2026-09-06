using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Pins the dispatch id every shipped connector answers. <c>ConnectorSyncService</c> resolves an
/// executor by matching a trigger's id against <see cref="IConnectorSyncExecutor.ConnectorId"/>, and
/// that id is derived from the registration attribute rather than written down — so a rename of a
/// connector's <c>ConnectorName</c> silently moves its API route segment and orphans the
/// <c>connector_name</c> rows already stored under the old one.
/// </summary>
public partial class ConnectorSyncExecutorRegistrationTests
{
    /// <summary>
    /// The id each connector dispatches on. <c>librelinkup</c> is the FreeStyle connector's
    /// <c>ConnectorName</c> lowered; its <c>DataSourceId</c> ("libre") is a different key and is not
    /// interchangeable with it.
    /// </summary>
    private static readonly string[] Expected =
    [
        "carelink", "dexcom", "eversense", "glooko", "gluroo", "librelinkup", "myfitnesspal",
        "mylife", "nightscout", "nocturneremote", "tandem", "tidepool", "twiist",
    ];

    public static TheoryData<string> ExpectedConnectorIds() => [.. Expected];

    [Theory]
    [MemberData(nameof(ExpectedConnectorIds))]
    public void EveryShippedConnector_RegistersAnExecutorUnderItsId(string connectorId)
    {
        RegisteredConnectorIds().Should().Contain(connectorId);
    }

    [Fact]
    public void NoOtherExecutorIsRegistered()
    {
        // Also guards the theory: an empty registration set would leave every case above vacuous.
        RegisteredConnectorIds().Should().BeEquivalentTo(Expected);
    }

    /// <summary>
    /// The id is an API route segment and a stored key, so it has to survive both without escaping.
    /// Nothing constrains the <c>ConnectorName</c> it is lowered from — a display-friendly rename to
    /// "Nocturne Remote" would yield "nocturne remote" — so the constraint lives here.
    /// </summary>
    [Fact]
    public void EveryConnectorId_IsUrlSafe()
    {
        RegisteredConnectorIds().Should().OnlyContain(id => UrlSafeId().IsMatch(id));
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex UrlSafeId();

    private static List<string> RegisteredConnectorIds()
    {
        var services = new ServiceCollection();
        services.AddConnectors(new ConfigurationBuilder().Build());

        return [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(IConnectorSyncExecutor))
            .Select(descriptor => (IConnectorSyncExecutor)Activator.CreateInstance(
                descriptor.ImplementationType!)!)
            .Select(executor => executor.ConnectorId)];
    }
}
