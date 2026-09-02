using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Pins what <c>AddConnector</c> registers for the connectors that install through
/// <see cref="ConnectorInstaller{TConfig,TService,TTokenProvider}"/>. One body serves all nine, so a
/// single edit there arms or disarms every one of them at once, and neither outcome shows up until
/// a sync runs.
/// </summary>
public class PatternConnectorInstallerTests
{
    private static readonly string[] Pattern =
    [
        "CareLink", "Dexcom", "Eversense", "Glooko", "LibreLinkUp", "MyFitnessPal", "Tandem",
        "Tidepool", "Twiist",
    ];

    public static TheoryData<string> PatternConnectors() => [.. Pattern];

    [Fact]
    public void TheStandardConnectors_InstallThroughTheSharedBase()
    {
        // Also guards the theories: a connector that stops deriving from the base drops out of
        // every case below rather than failing one.
        PatternInstallers().Select(installer => installer.ConnectorName)
            .Should().BeEquivalentTo(Pattern);
    }

    /// <summary>
    /// The token provider has to come out of the sync's own scope, and only
    /// <c>AddConnectorTokenProvider</c> registers it that way.
    /// <c>AddHttpClient&lt;TTokenProvider&gt;</c> has already registered the same type as transient,
    /// so dropping the scoped registration leaves one that still resolves — with the framework's
    /// bare HttpClient injection instead of the named client and the DI dependencies the provider
    /// declares.
    /// </summary>
    [Theory]
    [MemberData(nameof(PatternConnectors))]
    public void AnEnabledConnector_RegistersItsTokenProviderScoped(string connectorName)
    {
        var installer = PatternInstallers().Single(i => i.ConnectorName == connectorName);

        var services = new ServiceCollection();
        installer.Install(services, Configuration(connectorName, enabled: true));

        var registrations = services
            .Where(descriptor => descriptor.ServiceType == TypeArgument(installer, 2))
            .ToList();

        registrations.Should().NotBeEmpty("{0} registers a token provider", connectorName);

        // Last registration wins on resolution, so that is the one whose lifetime the sync gets.
        registrations[^1].Lifetime.Should().Be(ServiceLifetime.Scoped,
            "{0}'s token provider must resolve from the sync scope; the transient AddHttpClient " +
            "registration underneath it resolves too, so losing the scoped one is silent",
            connectorName);
    }

    /// <summary>
    /// A connector nobody turned on must register nothing a sync could reach. The frozen startup
    /// defaults are the exception: they are recorded whatever the connector's state, because the
    /// configuration surface reads them to show what could be enabled.
    /// </summary>
    /// <remarks>
    /// A sync executor is what a manual trigger and the poller both dispatch on, so one registered
    /// for a disabled connector runs a sync with no credentials configured, and the poller check is
    /// no protection — it re-reads the configuration itself and would stand its own loop down while
    /// the trigger route stayed live.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PatternConnectors))]
    public void ADisabledConnector_RegistersOnlyItsFrozenConfiguration(string connectorName)
    {
        var installer = PatternInstallers().Single(i => i.ConnectorName == connectorName);

        var services = new ServiceCollection();
        installer.Install(services, Configuration(connectorName, enabled: false));

        services.Should().ContainSingle(
                "a disabled {0} registers nothing beyond its frozen startup defaults", connectorName)
            .Which.ServiceType.Should().Be(
                typeof(IConnectorRegistration<>).MakeGenericType(TypeArgument(installer, 0)));
    }

    private static IConfiguration Configuration(string connectorName, bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Parameters:Connectors:{connectorName}:Enabled"] = enabled.ToString(),
                [$"Connectors:{connectorName}:Enabled"] = enabled.ToString(),
            })
            .Build();

    private static List<IConnectorInstaller> PatternInstallers() =>
        [.. ConnectorInstallers.Discover().Where(i => SharedBaseOf(i.GetType()) is not null)];

    /// <summary>
    /// The installer's shared-base type arguments: configuration at 0, service at 1, token provider
    /// at 2.
    /// </summary>
    private static Type TypeArgument(IConnectorInstaller installer, int index) =>
        SharedBaseOf(installer.GetType())!.GetGenericArguments()[index];

    private static Type? SharedBaseOf(Type installer)
    {
        for (var current = installer; current is not null; current = current.BaseType)
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(ConnectorInstaller<,,>))
                return current;

        return null;
    }
}
