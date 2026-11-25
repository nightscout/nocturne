using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Connectors.Twiist.Services;
using Nocturne.Connectors.Twiist.Tests.Mappers;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Services;

public class TwiistConnectorServiceTests
{
    private const string PwdId = "7c4a6533-b8db-4cc4-823e-6ac9c99e07e5";

    [Fact]
    public async Task SyncDataAsync_AutoDiscoversSinglePatient_FetchesThatPackage()
    {
        var overviews = new List<TwiistOverview> { new() { PwdId = PwdId, PwdNickname = "Pat" } };
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(overviews),
            ["/package"] = Json(new TwiistPackage { PwdId = PwdId, Status = new TwiistStatus() })
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        // The package was fetched for the auto-discovered id, not an empty path.
        fixture.RequestedPaths.Should().Contain(p => p.Contains($"/pwd/{PwdId}/package"));
    }

    [Fact]
    public async Task SyncDataAsync_NoFollowedPatients_ReportsUnhealthyWithGuidance()
    {
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(new List<TwiistOverview>())
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("not following anyone");
    }

    [Fact]
    public async Task SyncDataAsync_OverviewsUnreachable_ReportsFailureWithoutCredentialWording()
    {
        // The overviews call failed at the transport layer, not as a refused credential, so the
        // error must not send the tenant to re-enter a working password.
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().NotContain("password").And.NotContain("email");
    }

    [Fact]
    public async Task SyncDataAsync_MultipleFollowedPatients_ReportsUnhealthyWithNames()
    {
        var overviews = new List<TwiistOverview>
        {
            new() { PwdId = PwdId, PwdNickname = "Pat" },
            new() { PwdId = "11111111-2222-3333-4444-555555555555", PwdNickname = "Sam" }
        };
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(overviews)
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("Pat").And.Contain("Sam");
    }

    [Fact]
    public async Task SyncDataAsync_ConfiguredPatientId_SkipsOverviewDiscovery()
    {
        var fixture = new ServiceFixture(
            responses: new()
            {
                ["/package"] = Json(new TwiistPackage { PwdId = PwdId, Status = new TwiistStatus() })
            },
            patientId: PwdId);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.RequestedPaths.Should().NotContain(p => p.Contains(TwiistConstants.OverviewsPath));
        fixture.RequestedPaths.Should().Contain(p => p.Contains($"/pwd/{PwdId}/package"));
    }

    [Fact]
    public async Task SyncDataAsync_NarrowedRequest_SyncsOnlyTheRequestedType()
    {
        var fixture = new ServiceFixture(
            responses: new()
            {
                ["/package"] = Json(new TwiistPackage { PwdId = PwdId, Status = new TwiistStatus() })
            },
            patientId: PwdId);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.CarbIntake] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        // ItemsSynced carries a key per type the run looked at, so the untouched three must be absent.
        result.ItemsSynced.Keys.Should().Equal(SyncDataType.CarbIntake);
    }

    [Fact]
    public async Task SyncDataAsync_UnfilteredRequest_SyncsEveryEnabledType()
    {
        var fixture = new ServiceFixture(
            responses: new()
            {
                ["/package"] = Json(new TwiistPackage { PwdId = PwdId, Status = new TwiistStatus() })
            },
            patientId: PwdId);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), fixture.Config, CancellationToken.None);

        // Glucose is absent from an empty package either way: it bails before publishing when the
        // blob decodes to nothing, so it cannot tell a narrowed run from an unfiltered one.
        result.ItemsSynced.Keys.Should().Contain(
            [SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.TempBasals]);
    }

    [Fact]
    public async Task SyncDataAsync_PackageNotFound_ReportsUnhealthy()
    {
        var overviews = new List<TwiistOverview> { new() { PwdId = PwdId, PwdNickname = "Pat" } };
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(overviews),
            ["/package"] = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"message\":\"Validation failed (uuid v 4 is expected)\"}")
            }
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("no data");
    }

    [Fact]
    public async Task SyncDataAsync_PackageRequestTimesOut_IsAFailedRunNotACancelledOne()
    {
        var fixture = new ServiceFixture(
            responses: new(), patientId: PwdId, timesOutOn: "/package");

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("timed out");
        fixture.RequestedPaths.Should().HaveCountGreaterThan(1, "a timeout is retried before the run gives up");
    }

    [Fact]
    public async Task SyncDataAsync_CancelledRun_PropagatesTheCancellationRatherThanFailing()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new ServiceFixture(
            responses: new(), patientId: PwdId, timesOutOn: "/package", cancellation: cancellation);

        var run = () => fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SyncDataAsync_SecondSyncOfTheSamePackage_StoresNoNewRow()
    {
        var at = new DateTime(2026, 6, 16, 22, 42, 0, DateTimeKind.Utc);
        var store = new StoringConnectorPublisher();
        var package = new TwiistPackage
        {
            PwdId = PwdId,
            Status = new TwiistStatus
            {
                Summary = new TwiistSummary { LastGlucoseTrend = "Flat" },
                Details = new TwiistDetails { BasalRateUnitsPerHour = "0.4" },
                GlucoseHistory = new TwiistRawBlob { Data = GlucoseBlob((at, 120), (at.AddMinutes(5), 124)) },
                InsulinDelivery = new TwiistRawBlob { Data = TwiistTempBasalMapperTests.BuildBlob((at, at.AddMinutes(5), 0.5)) },
                InsulinHistory =
                [
                    new() { Identifier = "dose-1", DoseType = "Bolus", StartDate = at, Value = 1.5m },
                    new() { DoseType = "Suspend", StartDate = at.AddMinutes(30) },
                    new() { DoseType = "Resume", StartDate = at.AddMinutes(50) },
                ],
                MealHistory = [new TwiistMeal { StartDate = at, Grams = 30m }],
            },
        };
        var fixture = new ServiceFixture(
            responses: new() { ["/package"] = Json(package) }, patientId: PwdId, publisher: store);
        var request = new SyncRequest
        {
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.TempBasals],
        };

        await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        await fixture.Service.SyncDataAsync(request, fixture.Config, CancellationToken.None);

        stored.Select(k => k.Type).Distinct().Should().BeEquivalentTo(
            [nameof(SensorGlucose), nameof(Bolus), nameof(CarbIntake), nameof(TempBasal)]);
        stored.Should().HaveCount(2 + 1 + 1 + 2);
        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(o => o.Key, o => o.Value * 2));
        store.Unkeyed.Should().BeEmpty();
        store.Stored.Keys.Should().BeEquivalentTo(stored);
    }

    private static string GlucoseBlob(params (DateTime At, double Mgdl)[] readings)
    {
        var raw = new byte[readings.Length * 8];
        for (var i = 0; i < readings.Length; i++)
        {
            var span = raw.AsSpan(i * 8);
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)(readings[i].At - TwiistConstants.BlobEpoch).TotalSeconds);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(readings[i].Mgdl * 100));
        }

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    private static HttpResponseMessage Json<T>(T content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json")
    };

    private sealed class FakeTwiistAuthTokenProvider(string? token) : TwiistAuthTokenProvider(
        new HttpClient(),
        new ConnectorTokenCache(),
        new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
        ResolvedTenant.Accessor(),
        NullLogger<TwiistAuthTokenProvider>.Instance,
        Mock.Of<IRetryDelayStrategy>())
    {
        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TwiistConnectorConfiguration config, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                token is null ? (null, DateTime.MinValue, null) : (token, DateTime.UtcNow.AddHours(1), null));
    }

    private sealed class ServiceFixture
    {
        public TwiistConnectorService Service { get; }
        public TwiistConnectorConfiguration Config { get; }
        public IReadOnlyList<string> RequestedPaths => [.. _handler.Requests.Select(r => r.Uri!.PathAndQuery)];

        private readonly FakeHttpHandler _handler = new();

        public ServiceFixture(
            Dictionary<string, HttpResponseMessage> responses,
            string? token = "valid-token",
            string patientId = "",
            string? timesOutOn = null,
            CancellationTokenSource? cancellation = null,
            IConnectorPublisher? publisher = null)
        {
            Config = new TwiistConnectorConfiguration
            {
                Username = "follower@example.com",
                Password = "pw",
                PatientId = patientId
            };

            if (timesOutOn is not null)
                _handler.On(timesOutOn, _ =>
                {
                    cancellation?.Cancel();
                    throw new TaskCanceledException("The request timed out.");
                });
            foreach (var (path, response) in responses)
                _handler.On(path, response);

            var httpClient = new HttpClient(_handler);
            if (publisher is null)
            {
                var mock = new Mock<IConnectorPublisher>();
                mock.Setup(p => p.IsAvailable).Returns(true);
                publisher = mock.Object;
            }

            Service = new TwiistConnectorService(
                httpClient,
                new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
                NullLogger<TwiistConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                new FakeTwiistAuthTokenProvider(token),
                publisher);
        }
    }
}
