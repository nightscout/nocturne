using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Pins what <see cref="TenantUrlConnectorInstaller{TConfig,TService}"/> registers for the
/// connectors whose base URL is supplied per tenant. One body serves all three, so a single edit
/// there arms or disarms every one of them at once, and neither outcome shows up until a sync runs.
/// </summary>
public class TenantUrlConnectorInstallerTests
{
    private const string ConfiguredUrl = "https://ns.example.invalid/";

    private static readonly string[] TenantUrl = ["Gluroo", "Nightscout", "NocturneRemote"];

    public static TheoryData<string> TenantUrlConnectors() => [.. TenantUrl];

    [Fact]
    public void TheTenantUrlConnectors_InstallThroughTheSharedBase()
    {
        // Also guards the theories: a connector that stops deriving from the base drops out of
        // every case below rather than failing one.
        TenantUrlInstallers().Select(ConnectorInstallers.NameOf)
            .Should().BeEquivalentTo(TenantUrl);
    }

    /// <summary>
    /// The whole registration set an enabled connector gets from the base, by service type,
    /// lifetime and implementation. A sync resolves every one of these, and a lifetime or
    /// implementation that quietly changes fails at request time rather than at build time.
    /// </summary>
    [Theory]
    [MemberData(nameof(TenantUrlConnectors))]
    public void AnEnabledConnector_RegistersTheSharedSet(string connectorName)
    {
        var installer = InstallerNamed(connectorName);
        var config = TypeArgument(installer, 0);
        var service = TypeArgument(installer, 1);

        var services = new ServiceCollection();
        services.AddLogging();
        installer.Install(services, Configuration(connectorName, enabled: true));

        (Type Service, ServiceLifetime Lifetime, Type? Implementation)[] expected =
        [
            (typeof(IConnectorRegistration<>).MakeGenericType(config), ServiceLifetime.Singleton,
                typeof(ConnectorRegistration<>).MakeGenericType(config)),
            (typeof(IConnectorServerResolver<>).MakeGenericType(config), ServiceLifetime.Singleton,
                typeof(ConnectorServerResolver<>).MakeGenericType(config)),
            (typeof(IConnectorConfigurationLoader<>).MakeGenericType(config), ServiceLifetime.Scoped,
                typeof(ConnectorConfigurationLoader<>).MakeGenericType(config)),
            (typeof(IConnectorTokenCache), ServiceLifetime.Singleton, typeof(ConnectorTokenCache)),
            (typeof(IConnectorCacheInvalidator), ServiceLifetime.Singleton, null),
            (typeof(IConnectorSyncExecutor), ServiceLifetime.Scoped,
                typeof(ConnectorSyncExecutor<,>).MakeGenericType(service, config)),
            (service, ServiceLifetime.Transient, null),
        ];

        foreach (var (serviceType, lifetime, implementation) in expected)
        {
            var descriptor = services.Should()
                .ContainSingle(d => d.ServiceType == serviceType,
                    "{0} registers exactly one {1}", connectorName, serviceType.Name)
                .Subject;

            descriptor.Lifetime.Should().Be(lifetime,
                "{0}'s {1} resolves from that scope", connectorName, serviceType.Name);

            if (implementation is null)
                continue;

            (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType())
                .Should().Be(implementation,
                    "{0}'s {1} is served by that type", connectorName, serviceType.Name);
        }
    }

    /// <summary>
    /// A connector nobody turned on must register nothing a sync could reach. The frozen startup
    /// defaults are the exception — the configuration surface reads them to show what could be
    /// enabled — as is anything a subclass registers unconditionally.
    /// </summary>
    [Theory]
    [MemberData(nameof(TenantUrlConnectors))]
    public void ADisabledConnector_RegistersNothingASyncCouldReach(string connectorName)
    {
        var installer = InstallerNamed(connectorName);
        var config = TypeArgument(installer, 0);

        var services = new ServiceCollection();
        installer.Install(services, Configuration(connectorName, enabled: false));

        services.Should().NotContain(d => d.ServiceType == typeof(IConnectorSyncExecutor),
            "a disabled {0} must not answer a manual sync trigger", connectorName);
        services.Should().NotContain(
            d => d.ServiceType == typeof(IConnectorConfigurationLoader<>).MakeGenericType(config),
            "a disabled {0} loads no per-tenant configuration", connectorName);
        services.Should().NotContain(d => d.ServiceType == typeof(IConnectorTokenCache),
            "a disabled {0} needs no token cache", connectorName);
        services.Should().Contain(
            d => d.ServiceType == typeof(IConnectorRegistration<>).MakeGenericType(config),
            "a disabled {0} still records its frozen startup defaults", connectorName);
    }

    /// <summary>
    /// Nightscout is the one subclass with a registration ahead of the enablement check: its
    /// compatibility proxy stack injects the startup configuration directly rather than through
    /// <see cref="IConnectorRegistration{TConfig}"/>, so that singleton has to be there whether or
    /// not anyone turned the connector on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nightscout_RegistersItsStartupConfigurationDirectly(bool enabled)
    {
        var installer = InstallerNamed("Nightscout");

        var services = new ServiceCollection();
        installer.Install(services, Configuration("Nightscout", enabled));

        services.Should().ContainSingle(
            d => d.ServiceType == typeof(NightscoutConnectorConfiguration),
            "the compatibility proxy stack resolves it directly, enabled or not");
    }

    /// <summary>
    /// A URL already in the startup configuration is baked into the typed client's base address.
    /// The per-connector service reads relative paths off it, so losing it turns every request into
    /// an invalid-URI throw at call time.
    /// </summary>
    [Theory]
    [MemberData(nameof(TenantUrlConnectors))]
    public void AConfiguredUrl_BecomesTheClientsBaseAddress(string connectorName)
    {
        BaseAddressOf(connectorName, ConfiguredUrl).Should().Be(new Uri(ConfiguredUrl));
    }

    /// <summary>
    /// The case that actually runs in production: the URL arrives from per-tenant configuration
    /// after startup, so the client is registered without a base address and the connector supplies
    /// absolute URIs itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(TenantUrlConnectors))]
    public void AnUnconfiguredUrl_LeavesTheClientWithoutABaseAddress(string connectorName)
    {
        BaseAddressOf(connectorName, url: null).Should().BeNull();
    }

    private static Uri? BaseAddressOf(string connectorName, string? url)
    {
        var installer = InstallerNamed(connectorName);
        var service = TypeArgument(installer, 1);

        var services = new ServiceCollection();
        services.AddLogging();
        installer.Install(services, Configuration(connectorName, enabled: true, url));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(service.Name)
            .BaseAddress;
    }

    private static IConfiguration Configuration(string connectorName, bool enabled, string? url = null)
    {
        var values = new Dictionary<string, string?>
        {
            [$"Parameters:Connectors:{connectorName}:Enabled"] = enabled.ToString(),
            [$"Connectors:{connectorName}:Enabled"] = enabled.ToString(),
        };

        if (url is not null)
            values[$"Parameters:Connectors:{connectorName}:Url"] = url;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConnectorInstaller InstallerNamed(string connectorName) =>
        TenantUrlInstallers().Single(i => ConnectorInstallers.NameOf(i) == connectorName);

    private static List<IConnectorInstaller> TenantUrlInstallers() =>
        [.. ConnectorInstallers.Discover().Where(i => SharedBaseOf(i.GetType()) is not null)];

    /// <summary>
    /// The installer's shared-base type arguments: configuration at 0, service at 1.
    /// </summary>
    private static Type TypeArgument(IConnectorInstaller installer, int index) =>
        SharedBaseOf(installer.GetType())!.GetGenericArguments()[index];

    private static Type? SharedBaseOf(Type installer) =>
        ConnectorInstallers.ClosedBaseOf(installer, typeof(TenantUrlConnectorInstaller<,>));
}
