using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Connectors.Eversense.Services;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Eversense.Tests.Services;

public class EversenseConnectorServiceTests
{
    #region SelectPatient Tests

    [Fact]
    public void SelectPatient_SinglePatient_AutoSelects()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "only@example.com", CurrentGlucose = 100, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: null);

        result.Should().NotBeNull();
        result!.UserName.Should().Be("only@example.com");
    }

    [Fact]
    public void SelectPatient_MultiplePatients_WithConfiguredUsername_SelectsMatch()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "alice@example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: "bob@example.com");

        result.Should().NotBeNull();
        result!.UserName.Should().Be("bob@example.com");
    }

    [Fact]
    public void SelectPatient_MultiplePatients_CaseInsensitiveMatch()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "Alice@Example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: "alice@example.com");

        result.Should().NotBeNull();
        result!.UserName.Should().Be("Alice@Example.com");
    }

    [Fact]
    public void SelectPatient_MultiplePatients_NoConfiguredUsername_ReturnsNull()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "alice@example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: null);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectPatient_MultiplePatients_UsernameNotFound_ReturnsNull()
    {
        var patients = new List<EversensePatientDatum>
        {
            new() { UserName = "alice@example.com", CurrentGlucose = 100, IsTransmitterConnected = true },
            new() { UserName = "bob@example.com", CurrentGlucose = 110, IsTransmitterConnected = true }
        };

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: "charlie@example.com");

        result.Should().BeNull();
    }

    [Fact]
    public void SelectPatient_EmptyList_ReturnsNull()
    {
        var patients = new List<EversensePatientDatum>();

        var result = EversenseConnectorService.SelectPatient(patients, patientUsername: null);

        result.Should().BeNull();
    }

    #endregion

    #region AuthenticateAsync Tests

    [Fact]
    public async Task AuthenticateAsync_ReturnsTrue()
    {
        // Arrange — AuthenticateAsync is a legacy no-op; actual auth happens per-tenant in sync flow
        var fixture = new ServiceFixture(tokenToReturn: "valid-token");

        // Act
        var result = await fixture.Service.AuthenticateAsync();

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region SyncDataAsync (PerformSyncInternalAsync) Tests

    [Fact]
    public async Task SyncDataAsync_SinglePatientWithValidGlucose_PublishesSensorGlucose()
    {
        // Arrange
        var patients = new List<EversensePatientDatum>
        {
            new()
            {
                UserName = "patient@example.com",
                CurrentGlucose = 120,
                GlucoseTrend = 3, // Flat
                CgTime = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),
                Units = 0, // mg/dL
                IsTransmitterConnected = true
            }
        };

        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);
        publisherMock.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients)
            },
            publisher: publisherMock.Object);

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().ContainKey(SyncDataType.Glucose).WhoseValue.Should().Be(1);

        publisherMock.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.Is<IEnumerable<SensorGlucose>>(sg => sg.Count() == 1),
            It.IsAny<string>(),
            It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A null in the patient list is one malformed record. It is dropped before the list is read, so
    /// the patient beside it still syncs rather than the run failing on every cycle.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_NullEntryInThePatientList_SyncsThePatientBesideIt()
    {
        var patients = new List<EversensePatientDatum?>
        {
            null,
            new()
            {
                UserName = "patient@example.com",
                CurrentGlucose = 120,
                GlucoseTrend = 3,
                CgTime = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),
                Units = 0,
                IsTransmitterConnected = true
            }
        };

        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);
        publisherMock.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var fixture = new ServiceFixture(
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients)
            },
            publisher: publisherMock.Object);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.ItemsSynced.Should().ContainKey(SyncDataType.Glucose).WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task SyncDataAsync_TransmitterDisconnected_StillPublishesLatestReading()
    {
        // Arrange
        var patients = new List<EversensePatientDatum>
        {
            new()
            {
                UserName = "patient@example.com",
                CurrentGlucose = 120,
                GlucoseTrend = 3,
                CgTime = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),
                Units = 0,
                IsTransmitterConnected = false
            }
        };

        var publisherMock = CreatePublisher();

        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients)
            },
            publisher: publisherMock.Object);

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert — the connection flag lags the reading, so a fresh CGTime is published anyway.
        result.Success.Should().BeTrue();
        result.ItemsSynced.Should().ContainKey(SyncDataType.Glucose).WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task SyncDataAsync_EmptyPatientList_ReportsUnhealthyWithGuidance()
    {
        // Arrange
        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);

        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(new List<EversensePatientDatum>())
            },
            publisher: publisherMock.Object);

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert — empty follow list is a setup problem, surfaced as unhealthy with instructions
        // rather than a silent successful empty sync.
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("not following anyone");

        publisherMock.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<string>(),
            It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncDataAsync_PatientListUnreachable_ReportsFailureWithoutCredentialWording()
    {
        // Arrange — the API call fails at the transport layer, not as a refused credential, so the
        // error must not send the tenant to re-enter a working password.
        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);

        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] =
                    new HttpResponseMessage(HttpStatusCode.NotFound)
            },
            publisher: publisherMock.Object);

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().NotContain("password").And.NotContain("email");
    }

    [Fact]
    public async Task SyncDataAsync_MultiplePatientsWithoutConfiguredUsername_ReportsUnhealthyWithNames()
    {
        // Arrange
        var patients = new List<EversensePatientDatum>
        {
            new()
            {
                UserName = "alice@example.com",
                CurrentGlucose = 120,
                GlucoseTrend = 3,
                CgTime = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"),
                Units = 0,
                IsTransmitterConnected = true
            },
            new()
            {
                UserName = "bob@example.com",
                CurrentGlucose = 130,
                GlucoseTrend = 4,
                CgTime = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                Units = 0,
                IsTransmitterConnected = true
            }
        };

        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);

        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients)
            },
            publisher: publisherMock.Object,
            patientUsername: null); // No patient username configured

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert — ambiguous selection is surfaced as unhealthy, naming the available patients
        // so the user knows which username to configure.
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("alice@example.com").And.Contain("bob@example.com");

        publisherMock.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<string>(),
            It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncDataAsync_WithHistory_PublishesHistoryAndLatestReading()
    {
        // Arrange
        var latestTime = new DateTime(2026, 10, 16, 10, 10, 0, DateTimeKind.Utc);
        var patients = new List<EversensePatientDatum>
        {
            CreateFollowedPatient(userId: 42, glucose: 125, cgTime: latestTime.ToString("O"))
        };
        var history = new List<EversenseUserEvent>
        {
            new() { EventTypeId = 1, Value = 110, EventDate = "2026-10-16T10:00:00" },
            new() { EventTypeId = 1, Value = 118, EventDate = "2026-10-16T10:05:00" },
            new() { EventTypeId = 1, Value = 124, EventDate = "2026-10-16T10:10:00" },
            new() { EventTypeId = 10, Value = 121, EventDate = "2026-10-16T10:06:00" },
        };

        List<SensorGlucose>? published = null;
        var publisherMock = CreatePublisher(records => published = records.ToList());

        var fixture = new ServiceFixture(
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients),
                [EversenseConstants.Endpoints.GetFollowingUserSensorGlucose] = CreateJsonResponse(history)
            },
            publisher: publisherMock.Object);

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert — the history row at the latest reading's instant is replaced by the latest reading,
        // which carries the trend; the calibration row is not glucose.
        result.Success.Should().BeTrue();
        published.Should().NotBeNull();
        published!.Select(r => r.Mgdl).Should().Equal(110, 118, 125);
        published.Last().Direction.Should().Be(GlucoseDirection.Flat);

        fixture.RequestedUris.Should().Contain(u =>
            u.AbsolutePath == EversenseConstants.Endpoints.GetFollowingUserSensorGlucose &&
            u.Query.Contains("UserID=42") &&
            u.Query.Contains("startDate=") &&
            u.Query.Contains("endDate="));
    }

    [Fact]
    public async Task SyncDataAsync_HistoryUnavailable_StillPublishesLatestReading()
    {
        // Arrange
        var patients = new List<EversensePatientDatum>
        {
            CreateFollowedPatient(userId: 42, glucose: 125, cgTime: DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"))
        };

        var publisherMock = CreatePublisher();

        var fixture = new ServiceFixture(
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients),
                [EversenseConstants.Endpoints.GetFollowingUserSensorGlucose] =
                    new HttpResponseMessage(HttpStatusCode.NotFound)
            },
            publisher: publisherMock.Object);

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert
        result.ItemsSynced.Should().ContainKey(SyncDataType.Glucose).WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task SyncDataAsync_OusServer_ReadsFromOusDataHost()
    {
        // Arrange
        var patients = new List<EversensePatientDatum>
        {
            CreateFollowedPatient(userId: 42, glucose: 125, cgTime: DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"))
        };

        var fixture = new ServiceFixture(
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients),
                [EversenseConstants.Endpoints.GetFollowingUserSensorGlucose] =
                    CreateJsonResponse(new List<EversenseUserEvent>())
            },
            publisher: CreatePublisher().Object,
            server: "OUS");

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert
        var ousHost = new Uri(EversenseConstants.Servers.OusData).Host;
        fixture.RequestedUris.Should().NotBeEmpty().And.OnlyContain(u => u.Host == ousHost);
    }

    #endregion

    #region History window Tests

    [Fact]
    public void HistoryWindows_WithoutResumePoint_CoversTheNowGraphWindow()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        var windows = EversenseConnectorService.HistoryWindows(from: null, to: null, now);

        windows.Should().ContainSingle()
            .Which.Should().Be((now - EversenseConnectorService.DefaultHistoryWindow, now.AddMinutes(5)));
    }

    [Fact]
    public void HistoryWindows_LongCatchUp_SplitsIntoContiguousDays()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var from = now.AddHours(-50);

        var windows = EversenseConnectorService.HistoryWindows(from, to: null, now);

        windows.Should().HaveCount(3);
        windows[0].Start.Should().Be(from);
        windows.Zip(windows.Skip(1)).Should().OnlyContain(pair => pair.First.End == pair.Second.Start);
        windows[^1].End.Should().Be(now.AddMinutes(5));
    }

    [Fact]
    public void HistoryWindows_ClampsToMaxHistoryDays()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        var windows = EversenseConnectorService.HistoryWindows(now.AddDays(-180), to: null, now);

        windows[0].Start.Should().Be(now.AddDays(-EversenseConnectorConfiguration.MaxHistoryDays));
    }

    #endregion

    #region Test Infrastructure

    private static EversensePatientDatum CreateFollowedPatient(int userId, int glucose, string cgTime) => new()
    {
        UserId = userId,
        UserName = "patient@example.com",
        CurrentGlucose = glucose,
        GlucoseTrend = 3,
        CgTime = cgTime,
        Units = 0,
        IsTransmitterConnected = true
    };

    private static Mock<IConnectorPublisher> CreatePublisher(Action<IEnumerable<SensorGlucose>>? onPublish = null)
    {
        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);
        publisherMock.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, string, WriteOrigin, CancellationToken>(
                (records, _, _, _) => onPublish?.Invoke(records))
            .ReturnsAsync(true);
        return publisherMock;
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(content);
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// Test-friendly token provider that returns a predetermined token without making HTTP calls.
    /// Overrides AcquireTokenAsync so the base class GetValidTokenAsync returns our token.
    /// </summary>
    private sealed class FakeEversenseAuthTokenProvider : EversenseAuthTokenProvider
    {
        private readonly string? _tokenToReturn;

        public FakeEversenseAuthTokenProvider(string? tokenToReturn)
            : base(
                new HttpClient(),
                new Nocturne.Connectors.Core.Services.ConnectorTokenCache(),
                new Nocturne.Connectors.Core.Services.ConnectorServerResolver<EversenseConnectorConfiguration>(null, null, null),
                new FakeTenantAccessor(),
                NullLogger<EversenseAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>())
        {
            _tokenToReturn = tokenToReturn;
        }

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            EversenseConnectorConfiguration config, CancellationToken cancellationToken)
        {
            if (_tokenToReturn != null)
                return Task.FromResult<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)>(
                    (_tokenToReturn, DateTime.UtcNow.AddHours(1), null));

            return Task.FromResult<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)>(
                (null, DateTime.MinValue, null));
        }

        private class FakeTenantAccessor : Nocturne.Core.Contracts.Multitenancy.ITenantAccessor
        {
            public bool IsResolved => true;
            public Guid TenantId => Guid.Empty;
            public Nocturne.Core.Contracts.Multitenancy.TenantContext? Context => null;
            public void SetTenant(Nocturne.Core.Contracts.Multitenancy.TenantContext context) { }
        }
    }

    /// <summary>
    /// Routes HTTP requests to preconfigured responses based on URL path matching.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses;

        public List<Uri> RequestedUris { get; } = [];

        public MockHttpMessageHandler(Dictionary<string, HttpResponseMessage>? responses = null)
        {
            _responses = responses ?? new Dictionary<string, HttpResponseMessage>();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri != null)
                RequestedUris.Add(request.RequestUri);
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            foreach (var kvp in _responses)
            {
                if (path.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(kvp.Value);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>
    /// Encapsulates all the dependencies needed to construct an EversenseConnectorService for testing.
    /// </summary>
    private sealed class ServiceFixture
    {
        private readonly MockHttpMessageHandler _handler;

        public EversenseConnectorService Service { get; }
        public EversenseConnectorConfiguration Config { get; }
        public IReadOnlyList<Uri> RequestedUris => _handler.RequestedUris;

        public ServiceFixture(
            string? tokenToReturn = "valid-token",
            Dictionary<string, HttpResponseMessage>? httpResponses = null,
            IConnectorPublisher? publisher = null,
            ILogger<EversenseConnectorService>? serviceLogger = null,
            string? patientUsername = null,
            string server = "US")
        {
            Config = new EversenseConnectorConfiguration
            {
                Username = "test@example.com",
                Password = "test-password",
                PatientUsername = patientUsername,
                Server = server,
            };

            _handler = new MockHttpMessageHandler(httpResponses);
            var httpClient = new HttpClient(_handler)
            {
                BaseAddress = new Uri("https://test.eversensedms.com")
            };

            var tokenProvider = new FakeEversenseAuthTokenProvider(tokenToReturn);
            var retryStrategy = Mock.Of<IRetryDelayStrategy>();
            var logger = serviceLogger ?? NullLogger<EversenseConnectorService>.Instance;

            Service = new EversenseConnectorService(
                httpClient,
                new Nocturne.Connectors.Core.Services.ConnectorServerResolver<EversenseConnectorConfiguration>(null, null, null),
                logger,
                retryStrategy,
                tokenProvider,
                publisher);
        }
    }

    #endregion
}
