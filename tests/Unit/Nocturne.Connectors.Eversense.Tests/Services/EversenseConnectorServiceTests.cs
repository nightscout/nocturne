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
    public async Task AuthenticateAsync_WhenTokenProviderReturnsToken_ReturnsTrue()
    {
        // Arrange
        var fixture = new ServiceFixture(tokenToReturn: "valid-token");

        // Act
        var result = await fixture.Service.AuthenticateAsync();

        // Assert
        result.Should().BeTrue();
        fixture.Service.FailedRequestCount.Should().Be(0);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTokenProviderReturnsNull_ReturnsFalse()
    {
        // Arrange
        var fixture = new ServiceFixture(tokenToReturn: null);

        // Act
        var result = await fixture.Service.AuthenticateAsync();

        // Assert
        result.Should().BeFalse();
        fixture.Service.FailedRequestCount.Should().Be(1);
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
                It.IsAny<CancellationToken>()))
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
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncDataAsync_TransmitterDisconnected_SkipsPublishAndReportsSuccess()
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

        var publisherMock = new Mock<IConnectorPublisher>();
        publisherMock.Setup(p => p.IsAvailable).Returns(true);

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
        result.ItemsSynced.Should().NotContainKey(SyncDataType.Glucose);

        publisherMock.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncDataAsync_EmptyPatientList_SkipsPublish()
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

        // Assert
        result.Success.Should().BeTrue();

        publisherMock.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncDataAsync_MultiplePatientsWithoutConfiguredUsername_SkipsPublishAndLogs()
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

        var loggerMock = new Mock<ILogger<EversenseConnectorService>>();

        var fixture = new ServiceFixture(
            tokenToReturn: "valid-token",
            httpResponses: new Dictionary<string, HttpResponseMessage>
            {
                [EversenseConstants.Endpoints.GetFollowingPatientList] = CreateJsonResponse(patients)
            },
            publisher: publisherMock.Object,
            serviceLogger: loggerMock.Object,
            patientUsername: null); // No patient username configured

        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        // Act
        var result = await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        publisherMock.Verify(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Verify a warning was logged about multiple patients
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Multiple patients")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Test Infrastructure

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
                Options.Create(new EversenseConnectorConfiguration
                {
                    Username = "test@example.com",
                    Password = "test-password",
                }),
                new HttpClient(),
                NullLogger<EversenseAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>())
        {
            _tokenToReturn = tokenToReturn;
        }

        protected override Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
            CancellationToken cancellationToken)
        {
            if (_tokenToReturn != null)
                return Task.FromResult<(string? Token, DateTime ExpiresAt)>(
                    (_tokenToReturn, DateTime.UtcNow.AddHours(1)));

            return Task.FromResult<(string? Token, DateTime ExpiresAt)>(
                (null, DateTime.MinValue));
        }
    }

    /// <summary>
    /// Routes HTTP requests to preconfigured responses based on URL path matching.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses;

        public MockHttpMessageHandler(Dictionary<string, HttpResponseMessage>? responses = null)
        {
            _responses = responses ?? new Dictionary<string, HttpResponseMessage>();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
        public EversenseConnectorService Service { get; }
        public EversenseConnectorConfiguration Config { get; }

        public ServiceFixture(
            string? tokenToReturn = "valid-token",
            Dictionary<string, HttpResponseMessage>? httpResponses = null,
            IConnectorPublisher? publisher = null,
            ILogger<EversenseConnectorService>? serviceLogger = null,
            string? patientUsername = null)
        {
            Config = new EversenseConnectorConfiguration
            {
                Username = "test@example.com",
                Password = "test-password",
                PatientUsername = patientUsername,
            };

            var handler = new MockHttpMessageHandler(httpResponses);
            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://test.eversensedms.com")
            };

            var tokenProvider = new FakeEversenseAuthTokenProvider(tokenToReturn);
            var retryStrategy = Mock.Of<IRetryDelayStrategy>();
            var logger = serviceLogger ?? NullLogger<EversenseConnectorService>.Instance;

            Service = new EversenseConnectorService(
                httpClient,
                logger,
                retryStrategy,
                tokenProvider,
                publisher);
        }
    }

    #endregion
}
