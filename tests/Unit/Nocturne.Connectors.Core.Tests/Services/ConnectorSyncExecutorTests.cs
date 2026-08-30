using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ConnectorSyncExecutorTests
{
    [Fact]
    public void ConnectorId_IsTheRegisteredNameLowered()
    {
        Executor<AcmeConfiguration>().ConnectorId.Should().Be("acmepump");
    }

    [Fact]
    public void ConnectorId_PrefersTheDerivedTypesOwnRegistration()
    {
        // A connector configured by subclassing another connector's config (Gluroo extends
        // Nightscout) inherits its attribute too, and dispatching to the parent's id would run
        // the wrong connector.
        Executor<DerivedConfiguration>().ConnectorId.Should().Be("acmepumpplus");
    }

    [Fact]
    public void ConnectorId_UnregisteredConfiguration_Throws()
    {
        var build = () => Executor<UnregisteredConfiguration>();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectorRegistrationAttribute*");
    }

    [Fact]
    public void ConnectorId_DerivedConfigurationWithoutItsOwnRegistration_Throws()
    {
        // Answering the parent's id here would register a second executor under it, and a trigger
        // resolves by enumeration order — one vendor's credentials fetching under another's trigger.
        var build = () => Executor<DerivedWithoutRegistrationConfiguration>();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectorRegistrationAttribute*");
    }

    [Fact]
    public void AddConnectorSyncExecutor_TwoExecutorsClaimingOneId_Throws()
    {
        var services = new ServiceCollection()
            .AddConnectorSyncExecutor<ConnectorSyncExecutor<StubService<AcmeConfiguration>, AcmeConfiguration>>();

        var register = () => services
            .AddConnectorSyncExecutor<ConnectorSyncExecutor<OtherStubService, AcmeConfiguration>>();

        register.Should().Throw<InvalidOperationException>().WithMessage("*acmepump*");
    }

    private static ConnectorSyncExecutor<StubService<TConfig>, TConfig> Executor<TConfig>()
        where TConfig : BaseConnectorConfiguration => new();

    [ConnectorRegistration("AcmePump", "acme-service", "ACME", "ConnectSource.Nightscout")]
    private class AcmeConfiguration : BaseConnectorConfiguration;

    [ConnectorRegistration("AcmePumpPlus", "acme-service", "ACME", "ConnectSource.Nightscout")]
    private sealed class DerivedConfiguration : AcmeConfiguration;

    private sealed class UnregisteredConfiguration : BaseConnectorConfiguration;

    private sealed class DerivedWithoutRegistrationConfiguration : AcmeConfiguration;

    /// <summary>A second service type over the same config, so two executors answer one id.</summary>
    private sealed class OtherStubService : StubService<AcmeConfiguration>;

    private class StubService<TConfig> : IConnectorService<TConfig>
        where TConfig : BaseConnectorConfiguration
    {
        public string ServiceName => nameof(StubService<TConfig>);
        public List<SyncDataType> SupportedDataTypes => [];
        public Task<bool> AuthenticateAsync() => Task.FromResult(true);
        public void Dispose() { }

        public Task<SyncResult> SyncDataAsync(
            SyncRequest request, TConfig config, CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null) =>
            Task.FromResult(new SyncResult());
    }
}
