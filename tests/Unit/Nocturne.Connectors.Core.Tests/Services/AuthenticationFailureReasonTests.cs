using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class AuthenticationFailureReasonTests
{
    private sealed class TestConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    private sealed class TestConnectorService : BaseConnectorService<TestConfig>
    {
        public TestConnectorService()
            : base(new HttpClient(),
                new ConnectorServerResolver<TestConfig>(null, null, null),
                NullLogger<TestConnectorService>.Instance)
        {
        }

        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync() => Task.FromResult(true);

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TestConfig config,
            CancellationToken cancellationToken)
            => Task.FromResult(new SyncResult { Success = true });

        public void FailAuthentication(string reason) => TrackFailedAuthentication(reason);
        public void Succeed() => TrackSuccessfulRequest();
        public SyncResult Refused() => AuthenticationFailedResult();
    }

    [Fact]
    public void RecordedReason_IsWhatTheRefusalReports()
    {
        var service = new TestConnectorService();

        service.FailAuthentication("Could not reach the site");

        service.Refused().Message.Should().Be("Could not reach the site");
    }

    [Fact]
    public void SuccessAfterRecordedReason_RefusalFallsBackToGenericWording()
    {
        var service = new TestConnectorService();

        service.FailAuthentication("Could not reach the site");
        service.Succeed();

        var result = service.Refused();
        result.Message.Should().Be("Authentication failed");
        result.Errors.Should().ContainSingle().Which.Should().Be("Authentication failed for test");
    }
}
