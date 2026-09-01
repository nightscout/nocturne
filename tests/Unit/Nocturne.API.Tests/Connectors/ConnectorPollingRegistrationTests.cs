using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Pins which connectors <c>AddConnectors</c> schedules. A connector installs, appears in the UI and
/// accepts credentials on the strength of its <see cref="ConnectorRegistrationAttribute"/> alone, and
/// nothing ties that to a poll loop — so one that is never scheduled shows as connected and then
/// syncs only when someone presses the button, with no error anywhere.
/// </summary>
public class ConnectorPollingRegistrationTests
{
    private static readonly string[] Polling =
    [
        "CareLink", "Dexcom", "Eversense", "Glooko", "Gluroo", "LibreLinkUp", "MyFitnessPal",
        "MyLife", "Nightscout", "NocturneRemote", "Tandem", "Tidepool", "Twiist",
    ];

    /// <summary>
    /// Connectors that never poll: they receive or emit data by another route, so having no hosted
    /// service is the design, not a gap. HomeAssistant is an outbound notify target the alert
    /// delivery pipeline pushes to.
    /// </summary>
    private static readonly string[] NonPollingByDesign = ["HomeAssistant"];

    public static TheoryData<string> PollingConnectors() => [.. Polling];

    [Theory]
    [MemberData(nameof(PollingConnectors))]
    public void EveryPollingConnector_IsScheduled(string connectorName)
    {
        ScheduledConnectorNames().Should().Contain(connectorName);
    }

    [Fact]
    public void NoConnectorIsScheduledTwiceAndNoneIsMissed()
    {
        // Also guards the theory: an empty registration set would leave every case above vacuous.
        ScheduledConnectorNames().Should().BeEquivalentTo(Polling);
    }

    [Fact]
    public void EveryInstalledConnector_EitherPollsOrIsExemptByDesign()
    {
        InstalledConnectorNames().Should().BeEquivalentTo([.. Polling, .. NonPollingByDesign]);
    }

    /// <summary>
    /// A connector that also runs a real-time listener subclasses the poller. It must be scheduled by
    /// that subclass and by nothing else: a second, generic poller for the same connector would run a
    /// competing sync loop over the same tenants.
    /// </summary>
    [Fact]
    public void ConnectorsWithTheirOwnSubclass_AreScheduledByIt()
    {
        var subclasses = HandWrittenPollers();

        subclasses.Select(t => t.Name).Should().BeEquivalentTo(
            "NightscoutConnectorBackgroundService", "NocturneRemoteConnectorBackgroundService");

        var scheduled = ScheduledPollers();
        foreach (var subclass in subclasses)
        {
            scheduled.Should()
                .ContainSingle(poller => PolledConfigurationOf(poller) == PolledConfigurationOf(subclass))
                .Which.Should().Be(subclass);
        }
    }

    /// <summary>
    /// A poller subclassing the abstract base without closing the generic compiles and reads as
    /// registered, but neither the scan nor the executor loop reaches it and its connector is polled
    /// by the generic instead, without the overrides the subclass declares. That is the shape the ten
    /// per-vendor classes had, so it is what rebasing an old branch or copying the old pattern
    /// reintroduces.
    /// </summary>
    [Fact]
    public void APollerThatStopsAtTheAbstractBase_FailsStartup()
    {
        var register = () => new ServiceCollection()
            .AddConnectors(new ConfigurationBuilder().Build(), typeof(FakePoller<,>));

        register.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(OrphanedPoller)}*");
    }

    [Theory]
    [InlineData("Parameters:Connectors:Dexcom:Enabled")]
    [InlineData("Connectors:Dexcom:Enabled")]
    public void DisablingOneConnector_StandsDownOnlyItsPoller(string key)
    {
        var scheduled = ScheduledConnectorNames(Configuration((key, "false")));

        scheduled.Should().NotContain("Dexcom").And.Contain("Glooko");
    }

    [Theory]
    [InlineData("Parameters:Connectors:Settings:Enabled")]
    [InlineData("Connectors:Settings:Enabled")]
    public void DisablingConnectorsGlobally_StandsDownEveryPoller(string key)
    {
        ScheduledConnectorNames(Configuration((key, "false"))).Should().BeEmpty();
    }

    /// <summary>
    /// The connector service has to come out of the sync's own scope: the scoped
    /// <c>NocturneDbContext</c> it publishes through is pinned to that scope's tenant, so a service
    /// resolved from the root provider would write one tenant's data under another.
    /// </summary>
    [Fact]
    public async Task ThePoller_SyncsThroughTheConnectorServiceInTheSyncScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<RecordingConnectorService>();
        await using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();

        var config = new PollingTestConfiguration();
        var reporter = Mock.Of<ISyncProgressReporter>();

        var result = await new ExposedPoller(root)
            .SyncAsync(scope.ServiceProvider, config, CancellationToken.None, reporter);

        result.Success.Should().BeTrue();

        scope.ServiceProvider.GetRequiredService<RecordingConnectorService>().Calls
            .Should().ContainSingle()
            .Which.Should().Be((config, (DateTime?)null, reporter));

        root.GetRequiredService<RecordingConnectorService>().Calls.Should().BeEmpty(
            "resolving from the root provider would escape the sync's tenant scope");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static List<string> ScheduledConnectorNames(IConfiguration? configuration = null) =>
        [.. ScheduledPollers(configuration)
            .Select(poller =>
                ConnectorRegistrationAttribute.DeclaredOn(PolledConfigurationOf(poller)).ConnectorName)];

    private static List<Type> ScheduledPollers(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddConnectors(
            configuration ?? new ConfigurationBuilder().Build(),
            typeof(ConnectorBackgroundService<,>));

        return [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType!)];
    }

    private static List<Type> HandWrittenPollers() =>
        [.. typeof(ConnectorBackgroundService<,>).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false }
                        && t.BaseType is { IsGenericType: true } baseType
                        && baseType.GetGenericTypeDefinition() == typeof(ConnectorBackgroundService<,>))];

    private static Type PolledConfigurationOf(Type poller)
    {
        for (var current = poller; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(ConnectorBackgroundService<,>))
                return current.GetGenericArguments()[1];
        }

        throw new InvalidOperationException($"{poller.Name} is not a connector poller.");
    }

    private static List<string> InstalledConnectorNames() =>
        [.. ConnectorInstallers.Types()
            .Select(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false)?.ConnectorName)
            .Where(name => name is not null)
            .Distinct()!];

    [ConnectorRegistration("PollingTest", "polling-test", "POLLINGTEST", "PollingTest")]
    private sealed class PollingTestConfiguration : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    private sealed class RecordingConnectorService : IConnectorService<PollingTestConfiguration>
    {
        public List<(PollingTestConfiguration Config, DateTime? Since, ISyncProgressReporter? Reporter)> Calls
        { get; } = [];

        public string ServiceName => nameof(RecordingConnectorService);

        public List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

        public Task<bool> AuthenticateAsync() => Task.FromResult(true);

        public Task<SyncResult> SyncDataAsync(
            SyncRequest request,
            PollingTestConfiguration config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null) => throw new NotSupportedException();

        public Task<SyncResult> SyncDataAsync(
            PollingTestConfiguration config,
            CancellationToken cancellationToken = default,
            DateTime? since = null,
            ISyncProgressReporter? progressReporter = null)
        {
            Calls.Add((config, since, progressReporter));
            return Task.FromResult(new SyncResult { Success = true });
        }

        public void Dispose() { }
    }

    /// <summary>
    /// The poller pair in miniature. Pointing the scan at this assembly keeps the shape it rejects
    /// out of the API.
    /// </summary>
    private abstract class FakePollerBase<TConfig> : BackgroundService
        where TConfig : BaseConnectorConfiguration
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class FakePoller<TService, TConfig> : FakePollerBase<TConfig>
        where TService : class, IConnectorService<TConfig>
        where TConfig : BaseConnectorConfiguration;

    private sealed class OrphanedPoller : FakePollerBase<PollingTestConfiguration>;

    private sealed class ExposedPoller(IServiceProvider serviceProvider)
        : ConnectorBackgroundService<RecordingConnectorService, PollingTestConfiguration>(
            serviceProvider,
            NullLogger<ConnectorBackgroundService<RecordingConnectorService, PollingTestConfiguration>>.Instance)
    {
        public Task<SyncResult> SyncAsync(
            IServiceProvider scopeProvider,
            PollingTestConfiguration config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter) =>
            PerformSyncAsync(scopeProvider, config, cancellationToken, progressReporter);
    }
}
