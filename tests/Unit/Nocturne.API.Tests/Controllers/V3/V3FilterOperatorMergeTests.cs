using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// Nightscout V3 merges every <c>field$op</c> parameter on one field into one operator object, so
/// two operators on a field intersect.
/// </summary>
[Trait("Category", "Unit")]
public class V3FilterOperatorMergeTests
{
    private const long From = 1727654400000;
    private const long To = 1727658000000;

    private static ControllerContext Context(string queryString) => new()
    {
        HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString(queryString) } },
    };

    [Theory]
    [InlineData("?date$gte=1727654400000&date$lte=1727658000000&sort$desc=date")]
    [InlineData("?date$lte=1727658000000&date$gte=1727654400000&sort$desc=date")]
    public async Task Entries_DateWindow_PassesBothBoundsToTheService(string queryString)
    {
        string? captured = null;
        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.GetEntriesWithAdvancedFilterAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string?, int, int, string?, string?, bool, CancellationToken>(
                (_, _, _, find, _, _, _) => captured = find)
            .ReturnsAsync(Array.Empty<Entry>());

        var controller = new EntriesController(
            Mock.Of<IDocumentProcessingService>(),
            entries.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetEntries();

        var query = FindQuery.Parse(captured);
        query.FromMills.Should().Be(From);
        query.ToMills.Should().Be(To);
    }

    [Theory]
    [InlineData("?created_at$gte=1727654400000&created_at$lte=1727658000000&eventType$eq=Note&eventType$ne=Bolus")]
    [InlineData("?eventType$ne=Bolus&eventType$eq=Note&created_at$lte=1727658000000&created_at$gte=1727654400000")]
    public async Task Treatments_WindowAndEqualityWithAnotherOperator_AllReachTheService(string queryString)
    {
        var query = FindQuery.Parse(await CaptureTreatmentsFind(queryString));
        query.FromMills.Should().Be(From);
        query.ToMills.Should().Be(To);
        query.Matches(new { eventType = "Note", created_at = From + 1000 }).Should().BeTrue();
        query.Matches(new { eventType = "Meal Bolus", created_at = From + 1000 }).Should().BeFalse();
    }

    [Theory]
    [InlineData("?carbs$gte=10&carbs$lte=50")]
    [InlineData("?carbs$lte=50&carbs$gte=10")]
    public async Task Treatments_TwoBoundsOnANonTimeField_BothReachTheService(string queryString)
    {
        var query = FindQuery.Parse(await CaptureTreatmentsFind(queryString));

        query.Matches(new { carbs = 30 }).Should().BeTrue();
        query.Matches(new { carbs = 5 }).Should().BeFalse();
        query.Matches(new { carbs = 60 }).Should().BeFalse();
    }

    private static async Task<string?> CaptureTreatmentsFind(string queryString)
    {
        string? captured = null;
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.GetTreatmentsWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, string?, bool, CancellationToken>((_, _, find, _, _) => captured = find)
            .ReturnsAsync(Array.Empty<Treatment>());

        var controller = new TreatmentsController(
            Mock.Of<ITreatmentStore>(),
            Mock.Of<IDocumentProcessingService>(),
            treatments.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetTreatments();

        return captured;
    }

    [Theory]
    [InlineData("?created_at$gte=1727654400000&created_at$lte=1727658000000&device$eq=loop://iPhone")]
    [InlineData("?device$eq=loop://iPhone&created_at$lte=1727658000000&created_at$gte=1727654400000")]
    public async Task DeviceStatus_CreatedAtWindow_ReachesTheSnapshotRepository(string queryString)
    {
        var (from, to, device) = await CaptureDeviceStatusRead(queryString);

        from.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(From).UtcDateTime);
        to.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(To).UtcDateTime);
        device.Should().Be("loop://iPhone");
    }

    [Theory]
    [InlineData("?created_at$eq=1727654400000&created_at$lte=1727658000000")]
    [InlineData("?created_at$lte=1727658000000&created_at$eq=1727654400000")]
    public async Task DeviceStatus_EqualityWithABound_NarrowsToTheInstant(string queryString)
    {
        var (from, to, _) = await CaptureDeviceStatusRead(queryString);

        var instant = DateTimeOffset.FromUnixTimeMilliseconds(From).UtcDateTime;
        from.Should().Be(instant);
        to.Should().Be(instant);
    }

    [Theory]
    [InlineData("?device$eq=loop://iPhone&device$ne=openaps://rpi")]
    [InlineData("?device$ne=openaps://rpi&device$eq=loop://iPhone")]
    public async Task DeviceStatus_DeviceEqualityBesideAnotherOperator_StillNarrowsToTheDevice(string queryString)
    {
        var (_, _, device) = await CaptureDeviceStatusRead(queryString);

        device.Should().Be("loop://iPhone");
    }

    [Theory]
    [InlineData("?created_at$gt=1727654400000&created_at$lt=1727658000000")]
    [InlineData("?created_at$lt=1727658000000&created_at$gt=1727654400000")]
    public async Task DeviceStatus_StrictWindow_ExcludesTheBoundaryInstants(string queryString)
    {
        var (from, to, _) = await CaptureDeviceStatusRead(queryString);

        from.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(From + 1).UtcDateTime);
        to.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(To - 1).UtcDateTime);
    }

    private static async Task<(DateTime? From, DateTime? To, string? Device)> CaptureDeviceStatusRead(
        string queryString)
    {
        (DateTime? From, DateTime? To, string? Device) captured = default;
        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime?, DateTime?, string?, string?, int, int, bool, CancellationToken>(
                (from, to, device, _, _, _, _, _) => captured = (from, to, device))
            .ReturnsAsync(Array.Empty<ApsSnapshot>());

        var projection = new DeviceStatusProjectionService(
            apsRepo.Object,
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            NullLogger<DeviceStatusProjectionService>.Instance);

        var controller = new DeviceStatusController(
            projection,
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

        await controller.GetDeviceStatus();

        return captured;
    }

    [Theory]
    [InlineData("gte", "lte")]
    [InlineData("lte", "gte")]
    public void Converter_TwoBoundsOnOneField_FormOneOperatorObject(string first, string second)
    {
        var json = BaseV3Controller<Entry>.ConvertFilterCriteriaToFindQuery(
        [
            new V3FilterCriteria { Field = "sgv", Operator = first, Value = first == "gte" ? 100d : 180d },
            new V3FilterCriteria { Field = "sgv", Operator = second, Value = second == "gte" ? 100d : 180d },
        ]);

        var query = FindQuery.Parse(json);
        query.Matches(new { sgv = 140 }).Should().BeTrue();
        query.Matches(new { sgv = 90 }).Should().BeFalse();
        query.Matches(new { sgv = 200 }).Should().BeFalse();
    }

    [Fact]
    public void Converter_SoleEqualityAndSeparateFields_StayBareAndIndependent()
    {
        var json = BaseV3Controller<Entry>.ConvertFilterCriteriaToFindQuery(
        [
            new V3FilterCriteria { Field = "type", Operator = "eq", Value = "sgv" },
            new V3FilterCriteria { Field = "sgv", Operator = "gt", Value = 100d },
        ]);

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("type").GetString().Should().Be("sgv");
        doc.RootElement.GetProperty("sgv").GetProperty("$gt").GetDouble().Should().Be(100);
        FindQuery.Parse(json).GetEqualityValue("type").Should().Be("sgv");
    }
}
