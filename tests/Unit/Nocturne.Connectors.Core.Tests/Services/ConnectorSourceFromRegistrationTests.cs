using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ConnectorSourceFromRegistrationTests
{
    [ConnectorRegistration("SourceTest", "source-test", "SOURCE_TEST", "SourceTest", "source-test-connector")]
    public class RegisteredConfig : BaseConnectorConfiguration;

    [ConnectorRegistration("SourcelessTest", "sourceless-test", "SOURCELESS_TEST", "SourcelessTest")]
    public class SourcelessConfig : BaseConnectorConfiguration;

    public class DerivedFromRegisteredConfig : RegisteredConfig;

    private class Service<TConfig>(ConnectorServerResolver<TConfig> resolver)
        : BaseConnectorService<TConfig>(
            new HttpClient(), resolver, NullLogger.Instance, new RecordingRetryDelayStrategy())
        where TConfig : BaseConnectorConfiguration
    {
        public string Source => ConnectorSource;
        public override string ServiceName => "Test";

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TConfig config,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static Service<TConfig> Build<TConfig>() where TConfig : BaseConnectorConfiguration =>
        new(new ConnectorServerResolver<TConfig>(null, null, null));

    [Fact]
    public void ConnectorSource_IsTheRegisteredDataSourceId()
    {
        Build<RegisteredConfig>().Source.Should().Be("source-test-connector");
    }

    [Fact]
    public void ConnectorSource_RefusesARegistrationWithNoDataSourceId()
    {
        var read = () => Build<SourcelessConfig>().Source;

        read.Should().Throw<InvalidOperationException>().WithMessage("*SourcelessConfig*");
    }

    [Fact]
    public void ConnectorSource_IgnoresARegistrationInheritedFromAnotherConnector()
    {
        // Storing under an ancestor's source would file one connector's data as the other's.
        var read = () => Build<DerivedFromRegisteredConfig>().Source;

        read.Should().Throw<InvalidOperationException>();
    }
}
