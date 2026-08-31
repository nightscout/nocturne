using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Connectors.MyLife.Services;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

/// <summary>
/// Regression tests for <see cref="MyLifeConnectorService"/> authentication wiring. The connector
/// must establish its SOAP session by invoking the token provider during sync. A refactor previously
/// left that call out — <c>AuthenticateAsync</c> became a no-op and <c>PerformSyncInternalAsync</c>
/// only validated the session — so the session cache was never populated and every sync failed with
/// "session not established" regardless of credentials.
/// </summary>
public class MyLifeConnectorServiceTests
{
    [Fact]
    public async Task SyncDataAsync_EstablishesSession_WithValidCredentials()
    {
        var tenantId = Guid.NewGuid();
        using var http = new HttpClient(new SoapStubHandler(loginSucceeds: true));
        var (service, sessionCache, _) = MyLifeSyncHarness.BuildService(http, tenantId);

        var config = new MyLifeConnectorConfiguration
        {
            Username = "user@example.com",
            Password = "secret",
            PatientId = "patient-1"
        };
        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue("a valid login must establish the session and let the sync run");
        var session = sessionCache.Get(tenantId);
        session.Should().NotBeNull("the connector must populate the session cache via the token provider");
        session!.AuthToken.Should().Be("tok-123");
        session.ServiceUrl.Should().Be(MyLifeSyncHarness.ServiceUrl);
        session.PatientId.Should().Be("patient-1");
    }

    [Fact]
    public async Task SyncDataAsync_ReportsAuthFailure_WhenLoginFails()
    {
        var tenantId = Guid.NewGuid();
        using var http = new HttpClient(new SoapStubHandler(loginSucceeds: false));
        var (service, sessionCache, _) = MyLifeSyncHarness.BuildService(http, tenantId);

        var config = new MyLifeConnectorConfiguration
        {
            Username = "user@example.com",
            Password = "wrong",
            PatientId = "patient-1"
        };
        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeFalse("a failed login must surface as an unhealthy sync");
        result.Errors.Should().Contain(e => e.Contains("auth", StringComparison.OrdinalIgnoreCase));
        sessionCache.Get(tenantId).Should().BeNull("no session must be cached when login fails");
    }

    /// <summary>
    /// MyLife auth tokens are cached for 24 hours. One the server has already rejected must be
    /// dropped, or every sync until that expiry fails. Only a 401 drops it: the SOAP endpoints
    /// return other statuses for reasons that say nothing about the token.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task SyncDataAsync_DropsTheCachedToken_OnlyWhenMyLifeRejectsIt(
        HttpStatusCode syncStatus, bool expectTokenDropped)
    {
        var tenantId = Guid.NewGuid();
        using var http = new HttpClient(new SoapStubHandler(loginSucceeds: true, syncStatus: syncStatus));
        var (service, _, tokenProvider) = MyLifeSyncHarness.BuildService(http, tenantId);

        var config = new MyLifeConnectorConfiguration
        {
            Username = "user@example.com",
            Password = "secret",
            PatientId = "patient-1"
        };
        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        await service.SyncDataAsync(request, config, CancellationToken.None);

        tokenProvider.IsTokenExpired.Should().Be(expectTokenDropped);
    }

    /// <summary>
    /// A rejected state-span publish must fail the sync. The profile-derived spans used to discard
    /// the publish result outright, so a sync that wrote nothing still reported success.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenProfileStateSpanPublishIsRejected_ReportsFailure()
    {
        var tenantId = Guid.NewGuid();
        using var http = new HttpClient(new SoapStubHandler(loginSucceeds: true));

        // No basal programs, so only the state spans are mapped and the assertion cannot be
        // satisfied by the profile publish. No publisher is wired, so every publish is rejected.
        var readouts = new List<MyLifePumpSettingsReadout>
        {
            new()
            {
                Id = "readout-1",
                DeviceSerialNumber = "SN-1",
                ActiveBasalProgramName = "Day",
                UploadDateTime = 1767261600000L * 10_000
            }
        };
        var (service, _, _) = MyLifeSyncHarness.BuildService(http, tenantId,
            soapClient => new StubPumpSettingsSyncService(soapClient, readouts));

        var config = new MyLifeConnectorConfiguration
        {
            Username = "user@example.com",
            Password = "secret",
            PatientId = "patient-1"
        };
        var request = new SyncRequest { DataTypes = [SyncDataType.Profiles, SyncDataType.StateSpans] };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeFalse("state spans that never reached the tenant are not a successful sync");
        result.Errors.Should().Contain("StateSpans publish failed");
    }

    /// <summary>
    /// Returns fixed pump-settings readouts, bypassing the encrypted-archive fetch.
    /// </summary>
    private sealed class StubPumpSettingsSyncService(
        MyLifeSoapClient soapClient, IReadOnlyList<MyLifePumpSettingsReadout> readouts)
        : MyLifeSyncService(soapClient, NullLogger<MyLifeSyncService>.Instance)
    {
        public override Task<IReadOnlyList<MyLifePumpSettingsReadout>> FetchPumpSettingsAsync(
            string serviceUrl, string authToken, string patientId, CancellationToken cancellationToken)
            => Task.FromResult(readouts);
    }
}
