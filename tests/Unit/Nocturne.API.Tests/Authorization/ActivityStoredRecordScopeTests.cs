using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using V1 = Nocturne.API.Controllers.V1;
using V4 = Nocturne.API.Controllers.V4.Health;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The write guards check every record an id already names, as the real
/// <see cref="ActivityService"/> resolves them, against the real classifier. The storages are
/// mocked, so each test states which rows exist.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityStoredRecordScopeTests
{
    private const string ClientId = "60a1b2c3d4e5f67890123456";

    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<ISleepService> _sleep = new();
    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<IStepCountService> _stepCounts = new();
    private readonly ActivityDecomposer _decomposer = new(
        Mock.Of<INocturneDbContextFactory>(),
        Mock.Of<IStateSpanRepository>(),
        TimeProvider.System,
        NullLogger<ActivityDecomposer>.Instance);
    private readonly ActivityService _service;
    private readonly DefaultHttpContext _httpContext = new();

    public ActivityStoredRecordScopeTests()
    {
        var documents = new Mock<IDocumentProcessingService>();
        documents.Setup(d => d.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Returns((IEnumerable<Activity> a) => a);
        _service = new ActivityService(
            _stateSpans.Object,
            _sleep.Object,
            documents.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IDataEventSink<Activity>>(),
            _decomposer,
            _heartRates.Object,
            _stepCounts.Object,
            NullLogger<ActivityService>.Instance);
    }

    [Fact]
    public async Task V4Update_OfASleepSessionNamedByItsClientId_NeedsSleepScope()
    {
        Grant(Scope.TreatmentsReadWrite);
        StoreManualSleepSession(ClientId);

        var result = await V4Controller().UpdateActivity(
            ClientId, new UpsertActivityRequest { Type = "exercise", Mills = 1_780_000_000_000 });

        Forbidden(result.Result, Scope.SleepReadWrite);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task V1Save_OfASleepSessionNamedByItsClientId_NeedsSleepScope()
    {
        Grant(Scope.TreatmentsReadWrite);
        StoreManualSleepSession(ClientId);

        var result = await V1Controller().UpdateActivity(
            ClientId, new Activity { Type = "exercise", Mills = 1_780_000_000_000 });

        Forbidden(result.Result, Scope.SleepReadWrite);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task V4Delete_OfASleepSessionNamedByItsClientId_NeedsSleepScope()
    {
        Grant(Scope.TreatmentsReadWrite);
        StoreManualSleepSession(ClientId);

        var result = await V4Controller().DeleteActivity(ClientId);

        Forbidden(result, Scope.SleepReadWrite);
        VerifyNothingDeleted();
    }

    [Fact]
    public async Task V4Delete_OfAnIdNamingAStateSpanAndAHeartRate_NeedsBothScopes()
    {
        Grant(Scope.TreatmentsReadWrite);
        StoreStateSpanAndHeartRate(ClientId);

        var result = await V4Controller().DeleteActivity(ClientId);

        Forbidden(result, Scope.HeartRateReadWrite);
        VerifyNothingDeleted();
    }

    [Fact]
    public async Task V4Delete_OfAnIdNamingAStateSpanAndAHeartRate_DeletesBothWithBothScopes()
    {
        Grant(Scope.TreatmentsReadWrite, Scope.HeartRateReadWrite);
        StoreStateSpanAndHeartRate(ClientId);
        _stateSpans.Setup(s => s.DeleteActivityAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([ClientId]);
        _heartRates.Setup(s => s.DeleteHeartRateAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await V4Controller().DeleteActivity(ClientId);

        result.Should().BeOfType<NoContentResult>();
        _stateSpans.Verify(s => s.DeleteActivityAsync(ClientId, It.IsAny<CancellationToken>()), Times.Once);
        _heartRates.Verify(s => s.DeleteHeartRateAsync(ClientId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task V4Get_OfAnIdNamingASleepSessionAndAHeartRate_ReturnsTheOneTheCallerMayRead()
    {
        Grant(Scope.HeartRateRead);
        StoreManualSleepSession(ClientId);
        _heartRates.Setup(s => s.GetHeartRateByIdAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeartRate { Id = ClientId, Mills = 1, Bpm = 60 });

        var result = await V4Controller().GetActivity(ClientId);

        var record = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Activity>().Subject;
        record.Id.Should().Be(ClientId);
        record.AdditionalProperties!.Should().ContainKey("bpm");
    }

    [Fact]
    public async Task V1Get_OfASleepSessionNamedByItsClientId_IsHiddenWithoutSleepRead()
    {
        Grant(Scope.HeartRateRead);
        StoreManualSleepSession(ClientId);

        var result = await V1Controller().GetActivity(ClientId);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    private void StoreManualSleepSession(string clientId) =>
        _sleep.Setup(s => s.GetSessionBySourceRecordAsync(SleepSource.Manual, clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SleepSession
            {
                Id = Guid.CreateVersion7().ToString(),
                Source = SleepSource.Manual,
                OriginalId = clientId,
                StartTime = new DateTime(2026, 6, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 6, 2, 6, 0, 0, DateTimeKind.Utc),
            });

    private void StoreStateSpanAndHeartRate(string clientId)
    {
        _stateSpans.Setup(s => s.GetActivityByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Activity { Id = clientId, Type = "exercise", Mills = 1 });
        _heartRates.Setup(s => s.GetHeartRateByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeartRate { Id = clientId, Mills = 1, Bpm = 60 });
    }

    private void VerifyNothingWritten()
    {
        _sleep.Verify(s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()), Times.Never);
        _sleep.Verify(
            s => s.UpdateSessionAsync(It.IsAny<Guid>(), It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _stateSpans.Verify(
            s => s.UpdateActivityAsync(It.IsAny<string>(), It.IsAny<Activity>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _stateSpans.Verify(
            s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void VerifyNothingDeleted()
    {
        _stateSpans.Verify(s => s.DeleteActivityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _sleep.Verify(s => s.DeleteSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _heartRates.Verify(s => s.DeleteHeartRateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _stepCounts.Verify(s => s.DeleteStepCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void Forbidden(object? result, string missingScope)
    {
        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        System.Text.Json.JsonSerializer.Serialize(forbidden.Value).Should().Contain(missingScope);
    }

    private void Grant(params string[] scopes) =>
        _httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(scopes);

    private V4.ActivityController V4Controller() => new(_service, _decomposer)
    {
        ControllerContext = new ControllerContext { HttpContext = _httpContext },
    };

    private V1.ActivityController V1Controller() => new(
        _service, _decomposer, NullLogger<V1.ActivityController>.Instance, TimeProvider.System)
    {
        ControllerContext = new ControllerContext { HttpContext = _httpContext },
    };
}
