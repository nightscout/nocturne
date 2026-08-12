using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unit tests for the temp basal write endpoint: per-item upsert dispatch,
/// cancel truncation, and pre-persist validation.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalControllerTests
{
    private readonly Mock<ITempBasalRepository> _repo = new();
    private readonly Mock<IPatientDeviceStamper> _stamper = new();
    private readonly TempBasalController _controller;

    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public TempBasalControllerTests()
    {
        _controller = new TempBasalController(_repo.Object, _stamper.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _repo.Setup(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TempBasal m, WriteOrigin _, CancellationToken _) => m);
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TempBasal m, WriteOrigin _, CancellationToken _) => m);
    }

    private static CreateTempBasalRequest Request(
        double rate = 1.5, double? durationMinutes = 30, bool isCancel = false, DateTimeOffset? timestamp = null)
        => new()
        {
            Timestamp = timestamp ?? T0,
            Rate = rate,
            DurationMinutes = durationMinutes,
            IsCancel = isCancel,
            DataSource = "trio",
            SyncIdentifier = Guid.NewGuid().ToString(),
            Origin = TempBasalOrigin.Algorithm,
        };

    [Fact]
    public async Task CreateTempBasals_MapsDurationToEndTimestamp()
    {
        var result = await _controller.CreateTempBasals([Request(rate: 2.0, durationMinutes: 30)], CancellationToken.None);

        var created = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        created.Should().HaveCount(1);
        created[0].Rate.Should().Be(2.0);
        created[0].StartTimestamp.Should().Be(T0.UtcDateTime);
        created[0].EndTimestamp.Should().Be(T0.UtcDateTime.AddMinutes(30));
        created[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
    }

    [Fact]
    public async Task CreateTempBasals_Cancel_TruncatesActiveSpan()
    {
        var active = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = T0.UtcDateTime.AddMinutes(-10),
            EndTimestamp = null,
            Rate = 1.0,
        };
        _repo.Setup(r => r.GetActiveAtAsync(T0.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(active);

        var result = await _controller.CreateTempBasals([Request(isCancel: true)], CancellationToken.None);

        var returned = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        returned.Should().HaveCount(1);
        returned[0].EndTimestamp.Should().Be(T0.UtcDateTime);
        _repo.Verify(
            r => r.UpdateAsync(active.Id, It.Is<TempBasal>(m => m.EndTimestamp == T0.UtcDateTime), WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTempBasals_CancelWithNothingActive_IsNoOp()
    {
        _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TempBasal?)null);

        var result = await _controller.CreateTempBasals([Request(isCancel: true)], CancellationToken.None);

        var returned = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        returned.Should().BeEmpty();
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTempBasals_ProcessesItemsInTimestampOrder()
    {
        // A batch that starts a temp basal and cancels it later must apply the start first,
        // even when the array arrives out of order.
        var processed = new List<string>();
        _repo.Setup(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => processed.Add("create"))
            .ReturnsAsync((TempBasal m, WriteOrigin _, CancellationToken _) => m);
        _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback(() => processed.Add("cancel"))
            .ReturnsAsync((TempBasal?)null);

        var cancelLater = Request(isCancel: true, timestamp: T0.AddMinutes(15));
        var startFirst = Request(timestamp: T0);

        await _controller.CreateTempBasals([cancelLater, startFirst], CancellationToken.None);

        processed.Should().Equal("create", "cancel");
    }

    [Fact]
    public async Task CreateTempBasals_RejectsSyncIdentifierWithoutDataSource()
    {
        var request = Request();
        request.DataSource = null;

        var result = await _controller.CreateTempBasals([request], CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(400);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTempBasals_RejectsNegativeRate()
    {
        var result = await _controller.CreateTempBasals([Request(rate: -0.5)], CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateTempBasals_RejectsOversizedBatch()
    {
        var requests = Enumerable.Range(0, 1001).Select(_ => Request()).ToArray();

        var result = await _controller.CreateTempBasals(requests, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetAll_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        await _controller.GetAll(null, null, V4ReadLimits.MaxPageSize, 0);

        VerifyFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetAll_LimitAboveCeiling_IsClamped()
    {
        await _controller.GetAll(null, null, V4ReadLimits.MaxPageSize + 1, -1);

        VerifyFetched(V4ReadLimits.MaxPageSize, 0);
    }

    private void VerifyFetched(int limit, int offset) =>
        _repo.Verify(r => r.GetAsync(null, null, null, null, limit, offset, true, It.IsAny<CancellationToken>()), Times.Once);
}
