using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Every HTTP client a connector installer registers must carry
/// <see cref="LinkLocalGuardHandler"/> and have transport-level redirects off.
/// </summary>
/// <remarks>
/// The guard is installed by <c>ConfigureConnectorClient</c>, and nothing forces an installer to
/// call it — a bare <c>services.AddHttpClient&lt;T&gt;()</c> compiles, runs, and silently opts that
/// client out. MyLife registered all three of its clients that way while three docstrings claimed
/// full coverage, and Gluroo's no-URL branch did the same; both were live request-forgery paths for
/// any tenant member, because MyLife's <c>ServiceUrl</c> is member-supplied. A per-installer test
/// like this is the only thing that catches the next one: it fails when a connector is added
/// without the extension, which no amount of reviewing the guard itself would.
/// <para>
/// Installers are discovered by reflection over the loaded connector assemblies rather than listed,
/// so a new connector project is covered without editing this file. It lives in the API test
/// project because that is what transitively references every connector — the Connectors.Core test
/// project only sees Core, so the scan there would find nothing.
/// </para>
/// </remarks>
public class ConnectorClientGuardCoverageTests
{
    public static TheoryData<string> Installers()
    {
        var data = new TheoryData<string>();
        foreach (var installer in DiscoverInstallers())
            data.Add(installer.ConnectorName);
        return data;
    }

    [Fact]
    public void ConnectorInstallers_AreDiscovered()
    {
        // Guards the guard: if reflection finds nothing, every theory case below silently vanishes
        // and this file would pass while testing nothing at all.
        DiscoverInstallers().Should().HaveCountGreaterThan(5,
            "the connector installers are discovered by reflection; finding none would make the " +
            "coverage theory vacuous");
    }

    [Fact]
    public void ConnectorRegistryIsReadable()
    {
        // The other half of the same concern: client names come from an internal framework type by
        // reflection, so a rename would empty every case below without failing anything.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient<TypedClientProbe>();

        HttpClientNames(services).Should().Contain(nameof(TypedClientProbe),
            "client names are read from HttpClientMappingRegistry by reflection; if that stops " +
            "working the coverage theory passes while checking nothing");
    }

    [Theory]
    [MemberData(nameof(Installers))]
    public void EveryClientAConnectorRegisters_CarriesTheGuardAndNoTransportRedirects(
        string connectorName)
    {
        var installer = DiscoverInstallers().Single(i => i.ConnectorName == connectorName);

        var services = new ServiceCollection();
        services.AddLogging();
        installer.Install(services, EnablingConfiguration(connectorName));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        var clientNames = HttpClientNames(services);
        if (clientNames.Count == 0)
            return; // A push-only connector (e.g. Home Assistant) registers no outbound client.

        foreach (var clientName in clientNames)
        {
            var builder = new HandlerBuilder(provider);
            foreach (var configure in monitor.Get(clientName).HttpMessageHandlerBuilderActions)
                configure(builder);

            builder.AdditionalHandlers.Should().ContainItemsAssignableTo<LinkLocalGuardHandler>(
                "{0}'s '{1}' client must go through ConfigureConnectorClient; a bare " +
                "AddHttpClient opts it out of the link-local guard",
                connectorName, clientName);

            var primary = builder.PrimaryHandler;
            var followsRedirects = primary switch
            {
                SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
                HttpClientHandler legacy => legacy.AllowAutoRedirect,
                // A handler this test cannot read counts as following redirects rather than being
                // skipped: "redirects are off" and "the test could not tell" must not look alike.
                _ => true,
            };

            followsRedirects.Should().BeFalse(
                "{0}'s '{1}' client must have redirects off on its primary handler (a {2}); the " +
                "guard only ever sees the first URI, so a 3xx the transport follows is fetched " +
                "unwatched. An unrecognised handler type fails here too — a connector's primary " +
                "handler has to be one whose redirect behaviour this test can verify",
                connectorName, clientName, primary.GetType().Name);

            var pin = (primary as SocketsHttpHandler)?.ConnectCallback?.Target as PinnedConnector;

            pin.Should().NotBeNull(
                "{0}'s '{1}' client must connect through a PinnedConnector; without it the guard's " +
                "verdict binds the host name and the transport resolves it again for the socket, " +
                "so a name that answers differently the second time is reached anyway",
                connectorName, clientName);

            pin!.Policy.Should().Be(OutboundAddressPolicy.NotLinkLocal,
                "{0}'s '{1}' client is a connector: private and LAN targets are supported and only " +
                "link-local is refused",
                connectorName, clientName);
        }
    }

    private static List<IConnectorInstaller> DiscoverInstallers()
    {
        // Touch one type per connector assembly so they are loaded before the scan.
        _ = typeof(LinkLocalGuardHandler);
        foreach (var path in Directory.GetFiles(
                     AppContext.BaseDirectory, "Nocturne.Connectors.*.dll"))
        {
            try
            {
                Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly; nothing to scan.
            }
        }

        return [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Nocturne.Connectors.", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .Where(t => typeof(IConnectorInstaller).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IConnectorInstaller)Activator.CreateInstance(t)!)
            .GroupBy(i => i.ConnectorName)
            .Select(g => g.First())];
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>
    /// An installer returns early unless its connector is enabled, so the registrations never
    /// happen and the test would pass vacuously.
    /// </summary>
    private static IConfiguration EnablingConfiguration(string connectorName) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Parameters:Connectors:{connectorName}:Enabled"] = "true",
                [$"Connectors:{connectorName}:Enabled"] = "true",
            })
            .Build();

    /// <summary>
    /// Names of the HTTP clients registered on <paramref name="services"/>, read from the
    /// <c>HttpClientMappingRegistry</c> instance <c>AddHttpClient</c> puts in the collection.
    /// </summary>
    /// <remarks>
    /// Reflection over an internal type, because there is no public way to ask a service
    /// collection which client names exist, and the alternatives are worse: deriving names from
    /// type names would miss named-only clients, and reading
    /// <see cref="HttpClientFactoryOptions"/> cannot distinguish "registered with no handler
    /// configuration" from "never registered" — which is exactly the bare-AddHttpClient case this
    /// test exists to catch. If a framework upgrade renames the registry, this returns nothing and
    /// <see cref="ConnectorRegistryIsReadable"/> fails rather than the coverage silently emptying.
    /// </remarks>
    private static List<string> HttpClientNames(IServiceCollection services)
    {
        var registry = services
            .Select(d => d.ImplementationInstance)
            .FirstOrDefault(i => i?.GetType().Name == "HttpClientMappingRegistry");

        if (registry?.GetType().GetProperty("NamedClientRegistrations")?.GetValue(registry)
            is not System.Collections.IDictionary registrations)
        {
            return [];
        }

        return [.. registrations.Keys.Cast<object>()
            .Select(k => k as string ?? (k as Type)?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()!];
    }

    /// <summary>
    /// Stands in for a connector's typed client. The registry records typed registrations, which is
    /// what every connector uses — a named-only <c>AddHttpClient("x")</c> would not be covered, and
    /// no connector registers one.
    /// </summary>
    private sealed class TypedClientProbe(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    private sealed class HandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        // The framework's own default, and it allows redirects — so a client that configures no
        // primary handler is left holding one that follows a 3xx, and fails the redirect assertion
        // rather than being waved through.
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];
        public override IServiceProvider Services { get; } = services;
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
