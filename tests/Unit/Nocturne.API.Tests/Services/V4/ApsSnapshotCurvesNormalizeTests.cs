using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class ApsSnapshotCurvesNormalizeTests
{
    private static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static OpenApsPredBGs Curves(double start) => new()
    {
        IOB = [start, start - 5, start - 10],
        ZT = [start, start - 2],
        COB = [start, start + 5, start + 10, start + 5],
        UAM = [start, start + 10],
    };

    private static void SameJson(string? actual, string? expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected!)).Should().BeTrue("{0} should equal {1}", actual, expected);

    private static DeviceStatus OpenApsStatusWith(OpenApsSuggested? suggested, OpenApsEnacted? enacted) => new()
    {
        Mills = 1700000000000,
        Device = "openaps://Samsung",
        OpenAps = new OpenApsStatus { Suggested = suggested, Enacted = enacted },
    };

    private static UpsertApsSnapshotRequest UploadOf(DeviceStatus ds, V4Models.ApsSnapshot decomposed) => new()
    {
        Timestamp = new DateTimeOffset(decomposed.Timestamp, TimeSpan.Zero),
        AidAlgorithm = decomposed.AidAlgorithm,
        SuggestedJson = ds.OpenAps?.Suggested is { } s ? JsonSerializer.Serialize(s, s.GetType(), Camel) : null,
        EnactedJson = ds.OpenAps?.Enacted is { } e ? JsonSerializer.Serialize(e, e.GetType(), Camel) : null,
        LoopJson = ds.Loop is { } l ? JsonSerializer.Serialize(l, Camel) : null,
    };

    private static async Task<V4Models.ApsSnapshot> ThroughController(UpsertApsSnapshotRequest request)
    {
        var repo = new Mock<IApsSnapshotRepository>();
        IReadOnlyList<V4Models.ApsSnapshot> saved = [];
        repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<V4Models.ApsSnapshot>, WriteOrigin, CancellationToken>((records, _, _) => saved = [.. records])
            .ReturnsAsync(() => new BulkWrite<V4Models.ApsSnapshot>(saved, 0));
        var controller = new ApsSnapshotController(repo.Object, Mock.Of<ILoopClosedLoopStamper>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.CreateApsSnapshots([request]);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        return saved.Single();
    }

    private static void SameStoredRow(V4Models.ApsSnapshot actual, V4Models.ApsSnapshot expected)
    {
        SameJson(actual.SuggestedJson, expected.SuggestedJson);
        if (expected.EnactedJson is not null)
            SameJson(actual.EnactedJson, expected.EnactedJson);
        else
            actual.EnactedJson.Should().BeNull();
        actual.PredictedDefaultJson.Should().Be(expected.PredictedDefaultJson);
        actual.PredictedIobJson.Should().Be(expected.PredictedIobJson);
        actual.PredictedZtJson.Should().Be(expected.PredictedZtJson);
        actual.PredictedCobJson.Should().Be(expected.PredictedCobJson);
        actual.PredictedUamJson.Should().Be(expected.PredictedUamJson);
        actual.PredictedStartTimestamp.Should().Be(expected.PredictedStartTimestamp);
        actual.PredictedStartTimestamp.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_CommandsCarryingCurves_StoresTheRowTheDecomposerStores()
    {
        var ds = OpenApsStatusWith(
            new OpenApsSuggested { Bg = 120, Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120) },
            new OpenApsEnacted { Bg = 120, Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120), Rate = 0.8, Received = true });
        var decomposed = DeviceStatusDecomposer.MapToApsSnapshot(ds, null, null, null)!;

        var posted = await ThroughController(UploadOf(ds, decomposed));

        SameStoredRow(posted, decomposed);
        posted.SuggestedJson.Should().NotContain("predBGs");
        posted.EnactedJson.Should().NotContain("predBGs");
    }

    [Fact]
    public async Task Post_TrioCommandsCarryingCurves_KeepNoDefaultCurveLikeTheDecomposer()
    {
        var ds = OpenApsStatusWith(
            new OpenApsSuggested { Bg = 120, Timestamp = "2023-11-14T12:00:00Z", PredBGs = Curves(120) }, null);
        ds.OpenAps!.Version = "1.0.0";
        var decomposed = DeviceStatusDecomposer.MapToApsSnapshot(ds, null, null, null)!;

        var posted = await ThroughController(UploadOf(ds, decomposed));

        decomposed.AidAlgorithm.Should().Be(V4Models.AidAlgorithm.Trio);
        posted.PredictedDefaultJson.Should().BeNull().And.Be(decomposed.PredictedDefaultJson);
        SameStoredRow(posted, decomposed);
    }

    [Fact]
    public async Task Post_CommandWithoutCurvesBesideASiblingThatHasThem_StoresTheRowTheDecomposerStores()
    {
        var ds = OpenApsStatusWith(
            new OpenApsSuggested { Bg = 120, Timestamp = "2023-11-14T12:05:00Z", PredBGs = Curves(120) },
            new OpenApsEnacted { Bg = 119, Timestamp = "2023-11-14T12:00:00Z", Rate = 0.8, Received = true });
        var decomposed = DeviceStatusDecomposer.MapToApsSnapshot(ds, null, null, null)!;

        var posted = await ThroughController(UploadOf(ds, decomposed));

        SameStoredRow(posted, decomposed);
        posted.EnactedJson.Should().Contain("\"predBGs\":null");
    }

    [Fact]
    public async Task Post_LoopStatus_StoresTheRowTheDecomposerStores()
    {
        var ds = new DeviceStatus
        {
            Mills = 1700000000000,
            Device = "loop://iPhone",
            Loop = new LoopStatus
            {
                Predicted = new LoopPredicted { StartDate = "2023-11-14T12:00:00Z", Values = [120, 118.5, 116] },
            },
        };
        var decomposed = DeviceStatusDecomposer.MapToApsSnapshot(ds, null, null, null)!;

        var posted = await ThroughController(UploadOf(ds, decomposed));

        SameJson(posted.LoopJson, decomposed.LoopJson);
        posted.LoopJson.Should().NotContain("values");
        posted.PredictedDefaultJson.Should().Be(decomposed.PredictedDefaultJson);
        posted.PredictedStartTimestamp.Should().Be(decomposed.PredictedStartTimestamp).And.NotBeNull();
    }

    [Fact]
    public void Normalize_CurvesAlreadySplitAndCommandsWithoutTheKey_StillReadBackWithTheirCurves()
    {
        var uploaded = new V4Models.ApsSnapshot
        {
            Timestamp = new DateTime(2023, 11, 14, 12, 0, 0, DateTimeKind.Utc),
            PredictedIobJson = "[120,115,110]",
            PredictedZtJson = "[120,118]",
            SuggestedJson = """{"bg":120,"timestamp":"2023-11-14T12:00:00Z"}""",
            EnactedJson = """{"bg":120,"timestamp":"2023-11-14T12:00:00Z","rate":0.8,"received":true}""",
        };

        ApsSnapshotCurves.Normalize(uploaded);

        uploaded.SuggestedJson.Should().NotContain("predBGs");
        uploaded.EnactedJson.Should().NotContain("predBGs");
        var projected = DeviceStatusProjectionService.ProjectFromSnapshots(uploaded, null, null, null, null);
        projected.OpenAps!.Suggested!.PredBGs!.IOB.Should().Equal(120, 115, 110);
        projected.OpenAps.Enacted!.PredBGs!.ZT.Should().Equal(120, 118);
    }

    [Fact]
    public void Normalize_CurveThatDiffersFromTheColumns_IsKept()
    {
        var uploaded = new V4Models.ApsSnapshot
        {
            PredictedIobJson = "[1,2,3]",
            SuggestedJson = """{"bg":120,"predBGs":{"IOB":[9,9,9]}}""",
        };

        ApsSnapshotCurves.Normalize(uploaded);

        uploaded.SuggestedJson.Should().Contain("[9,9,9]");
        uploaded.PredictedIobJson.Should().Be("[1,2,3]");
    }

    [Fact]
    public void Normalize_LoopValuesThatDifferFromTheColumn_AreKept()
    {
        var uploaded = new V4Models.ApsSnapshot
        {
            PredictedDefaultJson = "[1,2]",
            LoopJson = """{"predicted":{"values":[120,118]}}""",
        };

        ApsSnapshotCurves.Normalize(uploaded);

        uploaded.LoopJson.Should().Contain("[120,118]");
        uploaded.PredictedDefaultJson.Should().Be("[1,2]");
    }

    [Fact]
    public void Normalize_LoopValuesWithANull_StoresTheBlobAsGiven()
    {
        const string loop = """{"predicted":{"values":[120,null]}}""";
        var uploaded = new V4Models.ApsSnapshot { LoopJson = loop };

        var normalize = () => ApsSnapshotCurves.Normalize(uploaded);

        normalize.Should().NotThrow();
        SameJson(uploaded.LoopJson, loop);
        uploaded.PredictedDefaultJson.Should().BeNull();
    }

    [Fact]
    public void Normalize_LoopValuesThatAreNotNumbers_StoreTheBlobAsGiven()
    {
        const string loop = """{"predicted":{"values":["a"]}}""";
        var uploaded = new V4Models.ApsSnapshot { LoopJson = loop };

        var normalize = () => ApsSnapshotCurves.Normalize(uploaded);

        normalize.Should().NotThrow();
        SameJson(uploaded.LoopJson, loop);
    }

    [Fact]
    public void Normalize_LoopBlobWithARepeatedKey_StoresTheBlobAsGiven()
    {
        const string loop = """{"predicted":{"values":[120]},"predicted":{"values":[121]}}""";
        var uploaded = new V4Models.ApsSnapshot { LoopJson = loop };

        var normalize = () => ApsSnapshotCurves.Normalize(uploaded);

        normalize.Should().NotThrow();
        uploaded.LoopJson.Should().Be(loop);
    }

    [Fact]
    public void Normalize_CommandWithARepeatedKeyAndCurves_StoresTheBlobAsGiven()
    {
        const string command = """{"bg":120,"bg":121,"predBGs":{"IOB":[120,115]}}""";
        var uploaded = new V4Models.ApsSnapshot { SuggestedJson = command };

        var normalize = () => ApsSnapshotCurves.Normalize(uploaded);

        normalize.Should().NotThrow();
        uploaded.SuggestedJson.Should().Be(command);
    }

    [Fact]
    public void Normalize_NullValuedKeys_AreDroppedExceptAnExplicitPredBGsNull()
    {
        var uploaded = new V4Models.ApsSnapshot
        {
            PredictedIobJson = "[120,115]",
            SuggestedJson = """{"bg":120,"reason":null,"predBGs":{"IOB":[120,115]}}""",
            EnactedJson = """{"bg":119,"rate":null,"requested":{"duration":null,"rate":0.5}}""",
        };

        ApsSnapshotCurves.Normalize(uploaded);

        SameJson(uploaded.SuggestedJson, """{"bg":120}""");
        SameJson(uploaded.EnactedJson, """{"bg":119,"requested":{"rate":0.5},"predBGs":null}""");
    }

    [Fact]
    public void Normalize_NoJsonOrMalformedJson_LeavesTheSnapshotAlone()
    {
        var uploaded = new V4Models.ApsSnapshot { SuggestedJson = "{not json", EnactedJson = "[]", LoopJson = null };

        ApsSnapshotCurves.Normalize(uploaded);

        uploaded.SuggestedJson.Should().Be("{not json");
        uploaded.EnactedJson.Should().Be("[]");
        uploaded.LoopJson.Should().BeNull();
    }
}
