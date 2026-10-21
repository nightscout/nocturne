using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A V3 search orders by the field it names and in the direction it asks for. Ascending is
/// Nightscout's default, and an ascending page is the oldest page, so a client paging forward
/// from a cursor loses nothing.
/// </summary>
[Trait("Category", "Unit")]
public class V3SortTests
{
    private const string SortConflictMessage = "Parameters sort and sort_desc cannot be combined";

    private static ControllerContext Context(string queryString) => new()
    {
        HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString(queryString) } },
    };

    private static ObjectResult AsObject(IActionResult result) =>
        result.Should().BeAssignableTo<ObjectResult>().Subject;

    private static List<JsonNode?> ResultItems(IActionResult result) =>
        JsonSerializer.SerializeToNode(AsObject(result).Value)!["result"]!.AsArray().ToList();

    private static EntriesController EntriesControllerFor(Mock<IEntryService> entries, string queryString) =>
        new(
            Mock.Of<IDocumentProcessingService>(),
            entries.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

    private static TreatmentsController TreatmentsControllerFor(Mock<ITreatmentService> treatments, string queryString) =>
        new(
            Mock.Of<IDocumentProcessingService>(),
            treatments.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

    private static ProfileController ProfileControllerFor(Mock<IProfileProjectionService> profiles, string queryString) =>
        new(
            profiles.Object,
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

    private static FoodController FoodControllerFor(Mock<IFoodRepository> foods, string queryString) =>
        new(
            foods.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<FoodController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

    private static DeviceStatusController DeviceStatusControllerFor(
        Mock<IDeviceStatusGroupRepository> groups, string queryString) =>
        new(
            new DeviceStatusProjectionService(
                groups.Object,
                Mock.Of<IApsSnapshotRepository>(),
                Mock.Of<IPumpSnapshotRepository>(),
                Mock.Of<IUploaderSnapshotRepository>(),
                Mock.Of<IStateSpanRepository>(),
                Mock.Of<IDeviceStatusExtrasRepository>(),
                NullLogger<DeviceStatusProjectionService>.Instance),
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = Context(queryString),
        };

    private static Mock<IEntryService> EntryServiceCapturing(Action<EntryQuery> capture)
    {
        var entries = new Mock<IEntryService>();
        entries
            .Setup(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EntryQuery, CancellationToken>((query, _) => capture(query))
            .ReturnsAsync(Array.Empty<Entry>());
        return entries;
    }

    private static Mock<ITreatmentService> TreatmentServiceCapturing(Action<TreatmentQuery> capture)
    {
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()))
            .Callback<TreatmentQuery, CancellationToken>((query, _) => capture(query))
            .ReturnsAsync(Array.Empty<Treatment>());
        return treatments;
    }

    [Theory]
    [InlineData("", true, ReadSortField.Time)]
    [InlineData("?sort=date", true, ReadSortField.Time)]
    [InlineData("?sort$desc=date", false, ReadSortField.Time)]
    [InlineData("?sort=sgv", true, ReadSortField.Sgv)]
    [InlineData("?sort$desc=sgv", false, ReadSortField.Sgv)]
    [InlineData("?sort=noSuchField", true, ReadSortField.Time)]
    public async Task Entries_CarryTheSortFieldAndDirectionToTheStore(
        string queryString, bool ascending, ReadSortField field)
    {
        EntryQuery? captured = null;

        await EntriesControllerFor(EntryServiceCapturing(q => captured = q), queryString).GetEntries();

        captured.Should().NotBeNull();
        captured!.ReverseResults.Should().Be(ascending);
        captured.SortField.Should().Be(field);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?sort=date", true)]
    [InlineData("?sort$desc=date", false)]
    public async Task Treatments_CarryTheDirectionToTheStore(string queryString, bool ascending)
    {
        TreatmentQuery? captured = null;

        await TreatmentsControllerFor(TreatmentServiceCapturing(q => captured = q), queryString).GetTreatments();

        captured.Should().NotBeNull();
        captured!.ReverseResults.Should().Be(ascending);
    }

    [Fact]
    public async Task Entries_SortWithSortDesc_IsRejectedBeforeTheStoreIsRead()
    {
        var entries = EntryServiceCapturing(_ => { });

        var read = () => EntriesControllerFor(entries, "?sort=date&sort$desc=date").GetEntries();

        await read.Should().ThrowAsync<V3SortConflictException>();
        entries.Verify(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Treatments_SortWithSortDesc_IsRejected()
    {
        var read = () => TreatmentsControllerFor(TreatmentServiceCapturing(_ => { }), "?sort=date&sort$desc=date")
            .GetTreatments();

        await read.Should().ThrowAsync<V3SortConflictException>();
    }

    [Fact]
    public async Task DeviceStatus_SortWithSortDesc_IsRejected()
    {
        var read = () => DeviceStatusControllerFor(new Mock<IDeviceStatusGroupRepository>(), "?sort=date&sort$desc=date")
            .GetDeviceStatus();

        await read.Should().ThrowAsync<V3SortConflictException>();
    }

    [Fact]
    public async Task Food_SortWithSortDesc_IsRejected()
    {
        var read = () => FoodControllerFor(new Mock<IFoodRepository>(), "?sort=name&sort$desc=name").GetFood();

        await read.Should().ThrowAsync<V3SortConflictException>();
    }

    [Fact]
    public async Task Profile_SortWithSortDesc_IsRejected()
    {
        var read = () => ProfileControllerFor(new Mock<IProfileProjectionService>(), "?sort=date&sort$desc=date")
            .GetProfiles();

        await read.Should().ThrowAsync<V3SortConflictException>();
    }

    [Theory]
    [MemberData(nameof(RejectedParameters))]
    public void ParameterFilter_AnswersARejectedParameterWith400AndNightscoutsBody(V3ParameterException exception, string message)
    {
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            [])
        {
            Exception = exception,
        };

        new V3ParameterExceptionFilterAttribute().OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var response = context.Result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(400);
        var body = JsonSerializer.SerializeToNode(response.Value)!;
        body["status"]!.GetValue<int>().Should().Be(400);
        body["message"]!.GetValue<string>().Should().Be(message);
    }

    public static TheoryData<V3ParameterException, string> RejectedParameters => new()
    {
        { new V3SortConflictException(), "Parameters sort and sort_desc cannot be combined" },
        { new V3ParameterOutOfToleranceException("limit"), "Parameter limit out of tolerance" },
    };

    [Fact]
    public void ParameterFilter_LeavesOtherExceptionsAlone()
    {
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            [])
        {
            Exception = new InvalidOperationException(),
        };

        new V3ParameterExceptionFilterAttribute().OnException(context);

        context.ExceptionHandled.Should().BeFalse();
    }

    [Fact]
    public async Task ReadingOneDocument_IgnoresASortConflict()
    {
        var foods = new Mock<IFoodRepository>();
        foods
            .Setup(f => f.GetFoodByIdAsync("a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Food { Id = "a", Name = "apple" });

        var result = await FoodControllerFor(foods, "?sort=name&sort$desc=name").GetFoodById("a");

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Entries_EmptySortParameterBesideSortDesc_IsNotAConflict()
    {
        EntryQuery? captured = null;

        await EntriesControllerFor(EntryServiceCapturing(q => captured = q), "?sort=&sort$desc=date").GetEntries();

        captured.Should().NotBeNull();
        captured!.ReverseResults.Should().BeFalse();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?sort=date", true)]
    [InlineData("?sort$desc=date", false)]
    public async Task Profile_AscendingPagesFromTheOldestProfile(string queryString, bool ascending)
    {
        var profiles = new Mock<IProfileProjectionService>();
        profiles
            .Setup(s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Profile>());
        profiles
            .Setup(s => s.CountProfilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await ProfileControllerFor(profiles, queryString).GetProfiles();

        profiles.Verify(
            s => s.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), !ascending, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?sort$desc=date", false)]
    public async Task DeviceStatus_AscendingReadsFromTheOldestEnd(string queryString, bool ascending)
    {
        var groups = new Mock<IDeviceStatusGroupRepository>();
        groups
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DeviceStatusGroup>());

        await DeviceStatusControllerFor(groups, queryString).GetDeviceStatus();

        groups.Verify(
            r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), !ascending, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static readonly Food[] Pantry =
    [
        new() { Id = "b", Name = "banana", Carbs = 27, SrvModified = 3000 },
        new() { Id = "c", Name = "cherry", Carbs = 12, SrvModified = 1000 },
        new() { Id = "a", Name = "apple", Carbs = 25, SrvModified = 2000 },
    ];

    private static async Task<string[]> FoodNames(string queryString)
    {
        var repository = new Mock<IFoodRepository>();
        repository
            .Setup(f => f.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pantry);

        var result = await FoodControllerFor(repository, queryString).GetFood();

        return ResultItems(result).Select(item => item!["name"]!.GetValue<string>()).ToArray();
    }

    [Theory]
    [InlineData("", new[] { "apple", "banana", "cherry" })]
    [InlineData("?sort=name", new[] { "apple", "banana", "cherry" })]
    [InlineData("?sort$desc=name", new[] { "cherry", "banana", "apple" })]
    [InlineData("?sort=carbs", new[] { "cherry", "apple", "banana" })]
    [InlineData("?sort$desc=carbs", new[] { "banana", "apple", "cherry" })]
    [InlineData("?sort=srvModified", new[] { "cherry", "apple", "banana" })]
    [InlineData("?sort$desc=srvModified", new[] { "banana", "apple", "cherry" })]
    [InlineData("?sort=noSuchField", new[] { "apple", "banana", "cherry" })]
    public async Task Food_OrdersByTheSortFieldAndDirection(string queryString, string[] expected)
    {
        (await FoodNames(queryString)).Should().Equal(expected);
    }

    [Fact]
    public async Task Food_PagesAfterOrdering()
    {
        (await FoodNames("?sort=carbs&limit=1&skip=1")).Should().Equal("apple");
    }
}
