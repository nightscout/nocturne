using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class SupportedDataTypesFromRegistrationTests
{
    [ConnectorRegistration(
        "registered-test",
        "Registered test connector",
        "RegisteredTest",
        "REGISTERED_TEST",
        "registered-test",
        "registered-test",
        SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.TempBasals])]
    public class RegisteredConfig : BaseConnectorConfiguration;

    public class UnregisteredConfig : BaseConnectorConfiguration;

    public class DerivedFromRegisteredConfig : RegisteredConfig;

    private class Service<TConfig>(ConnectorServerResolver<TConfig> resolver)
        : BaseConnectorService<TConfig>(new HttpClient(), resolver, NullLogger.Instance)
        where TConfig : BaseConnectorConfiguration
    {
        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";
        public override Task<bool> AuthenticateAsync() => Task.FromResult(true);

        // These tests only read SupportedDataTypes, never run a sync.
        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TConfig config,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static Service<TConfig> Build<TConfig>() where TConfig : BaseConnectorConfiguration =>
        new(new ConnectorServerResolver<TConfig>(null, null, null));

    [Fact]
    public void SupportedDataTypes_ComesFromTheRegistrationAttribute()
    {
        // The attribute drives the tenant's toggle schema. Reading the sync loop's list from the
        // same place is what stops a connector advertising a toggle it never acts on, or syncing
        // data the tenant cannot turn off.
        Build<RegisteredConfig>().SupportedDataTypes.Should().BeEquivalentTo(
            [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.TempBasals]);
    }

    [Fact]
    public void SupportedDataTypes_FallsBackToGlucoseWithoutARegistration()
    {
        Build<UnregisteredConfig>().SupportedDataTypes.Should().BeEquivalentTo([SyncDataType.Glucose]);
    }

    [Fact]
    public void SupportedDataTypes_IgnoresARegistrationInheritedFromAnotherConnector()
    {
        // Gluroo's config subclasses Nightscout's. Answering an ancestor's attribute would sync the
        // data types the other connector declared.
        Build<DerivedFromRegisteredConfig>().SupportedDataTypes.Should()
            .BeEquivalentTo([SyncDataType.Glucose]);
    }

    [Fact]
    public void SupportedDataTypes_HandsOutACopy()
    {
        // The list is materialised once per connector type; callers assign it onto SyncRequest,
        // so returning the shared instance would let one run's mutation reach every later run.
        var service = Build<RegisteredConfig>();

        service.SupportedDataTypes.Add(SyncDataType.Profiles);

        service.SupportedDataTypes.Should().NotContain(SyncDataType.Profiles);
    }
}
