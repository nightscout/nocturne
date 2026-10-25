using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

public class PebbleControllerTests
{
    private const long Now = 1_700_000_000_000;

    private readonly Mock<IEntryService> _entryService = new();
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<ITenantAppearanceReader> _appearance = new();
    private readonly Mock<IIobCobReader> _iobCob = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaders = new();
    private List<UploaderSnapshot> _uploaderSnapshotsNewestFirst = [];
    private int _requestedSgvCount;
    private int _requestedCalCount;

    private PebbleController NewController(string? tenantUnits, params Entry[] newestFirst)
    {
        _entryService
            .Setup(s => s.GetEntriesAsync("sgv", It.IsAny<int>(), 0, It.IsAny<CancellationToken>()))
            .Callback<string?, int, int, CancellationToken>((_, count, _, _) => _requestedSgvCount = count)
            .ReturnsAsync(() => newestFirst.Take(_requestedSgvCount).ToArray());
        _entryService
            .Setup(s => s.GetEntriesAsync("cal", It.IsAny<int>(), 0, It.IsAny<CancellationToken>()))
            .Callback<string?, int, int, CancellationToken>((_, count, _, _) => _requestedCalCount = count)
            .ReturnsAsync(Array.Empty<Entry>());

        _appearance
            .Setup(a => a.GetTenantAppearanceAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = tenantUnits });
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(TenantId);

        _uploaders
            .Setup(u => u.GetAsync(null, null, null, null, It.IsAny<int>(), 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _uploaderSnapshotsNewestFirst.AsEnumerable());

        return new PebbleController(
            _entryService.Object,
            _iobCob.Object,
            _uploaders.Object,
            _appearance.Object,
            tenantAccessor.Object,
            Mock.Of<ILogger<PebbleController>>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static Entry Sgv(double mgdl, int minutesAgo) =>
        new()
        {
            Type = "sgv",
            Mgdl = mgdl,
            Sgv = mgdl,
            Direction = "Flat",
            Mills = Now - minutesAgo * 60_000L,
        };

    private static async Task<PebbleResponse> Get(
        PebbleController controller,
        string? units = null,
        int count = 1
    )
    {
        var result = await controller.GetPebbleData(units, count);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<PebbleResponse>().Subject;
    }

    private static JsonElement FirstBg(PebbleResponse response) =>
        JsonSerializer
            .SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("bgs")[0];

    [Fact]
    public async Task Glucose_IsTheMgdlValueWhenItDiffersFromSgv()
    {
        var entry = Sgv(113, 0);
        entry.Sgv = 90;
        var controller = NewController("mg/dl", entry);

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("sgv").GetString().Should().Be("113");
    }

    [Fact]
    public async Task IobAndCob_ComeFromTheReader()
    {
        _iobCob
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobCobReading(
                new IobResult { Iob = 1.254, Source = "OpenAPS" },
                new CobResult { Cob = 17.8333333, Source = "OpenAPS" },
                new IobCobInputs([], [], [])));
        var controller = NewController("mg/dl", Sgv(113, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("iob").GetString().Should().Be("1.25");
        bg.GetProperty("cob").GetDouble().Should().Be(17.8);
    }

    [Fact]
    public async Task WhenTheReaderFails_TheGlucoseStillReturns()
    {
        _iobCob
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());
        var controller = NewController("mg/dl", Sgv(113, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("sgv").GetString().Should().Be("113");
        bg.GetProperty("iob").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Battery_IsTheNewestUploaderSnapshotThatHasOne()
    {
        _uploaderSnapshotsNewestFirst =
        [
            new UploaderSnapshot { Timestamp = DateTime.UtcNow, Battery = null },
            new UploaderSnapshot { Timestamp = DateTime.UtcNow.AddMinutes(-5), Battery = 64 },
            new UploaderSnapshot { Timestamp = DateTime.UtcNow.AddMinutes(-10), Battery = 90 },
        ];
        var controller = NewController("mg/dl", Sgv(113, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("battery").GetString().Should().Be("64");
    }

    [Fact]
    public async Task NoUploaderSnapshots_OmitsTheBattery()
    {
        var controller = NewController("mg/dl", Sgv(113, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("battery").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task NoUnitsParameter_UsesTenantMmolUnits()
    {
        var controller = NewController("mmol", Sgv(113, 0), Sgv(108, 5));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("sgv").GetString().Should().Be("6.3");
        bg.GetProperty("bgdelta").GetString().Should().Be("0.3");
    }

    [Fact]
    public async Task NoUnitsParameter_UsesTenantMgdlUnits()
    {
        var controller = NewController("mg/dl", Sgv(113, 0), Sgv(108, 5));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("sgv").GetString().Should().Be("113");
    }

    [Fact]
    public async Task NoUnitsParameter_AndNoOwnerPreference_UsesMgdl()
    {
        var controller = NewController(null, Sgv(113, 0), Sgv(108, 5));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("sgv").GetString().Should().Be("113");
    }

    [Theory]
    [InlineData("mmol", "mg/dl", "113")]
    [InlineData("mmol", "mg/dL", "113")]
    [InlineData("mmol", "mgdl", "113")]
    [InlineData("mg/dl", "mmol", "6.3")]
    [InlineData("mg/dl", "mmol/L", "6.3")]
    public async Task UnitsParameter_OverridesTenantUnits(string tenantUnits, string units, string expectedSgv)
    {
        var controller = NewController(tenantUnits, Sgv(113, 0), Sgv(108, 5));

        var bg = FirstBg(await Get(controller, units));

        bg.GetProperty("sgv").GetString().Should().Be(expectedSgv);
    }

    [Theory]
    [InlineData(36, 36)]
    [InlineData(PebbleController.MaxCount, PebbleController.MaxCount)]
    [InlineData(PebbleController.MaxCount + 1, PebbleController.MaxCount)]
    [InlineData(0, 1)]
    public async Task Count_IsHonouredUpToTheCap(int requested, int expectedBgs)
    {
        var entries = Enumerable
            .Range(0, PebbleController.MaxCount + 10)
            .Select(i => Sgv(100, i * 5))
            .ToArray();
        var controller = NewController("mg/dl", entries);

        var response = await Get(controller, count: requested);

        response.Bgs.Should().HaveCount(expectedBgs);
        _requestedCalCount.Should().Be(expectedBgs);
    }

    [Fact]
    public async Task BgdeltaInMgdl_IsAJsonNumber()
    {
        var controller = NewController("mg/dl", Sgv(113, 0), Sgv(108, 5));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").ValueKind.Should().Be(JsonValueKind.Number);
        bg.GetProperty("bgdelta").GetRawText().Should().Be("5");
    }

    [Fact]
    public async Task BgdeltaInMgdl_IsZeroNumberWhenThereIsNoDelta()
    {
        var controller = NewController("mg/dl", Sgv(113, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").GetRawText().Should().Be("0");
    }

    [Fact]
    public async Task BgdeltaInMmol_WithNoDelta_IsZeroToFixedOneAsUpstreamEmitsIt()
    {
        var controller = NewController("mmol", Sgv(113, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").GetString().Should().Be("0.0");
    }

    [Fact]
    public async Task BgdeltaAcrossAGapOverNineMinutes_IsInterpolatedToFiveMinutes()
    {
        var controller = NewController("mg/dl", Sgv(130, 0), Sgv(100, 15));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").GetRawText().Should().Be("10");
    }

    [Fact]
    public async Task BgdeltaOfAdjacentReadings_IsTheRawDifferenceOfTheNewestPair()
    {
        var controller = NewController("mg/dl", Sgv(120, 0), Sgv(110, 5), Sgv(60, 10));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").GetRawText().Should().Be("10");
    }

    [Fact]
    public async Task BgdeltaInMmol_NeverSerialisesNegativeZero()
    {
        var controller = NewController("mmol", Sgv(100, 0), Sgv(101, 15));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").GetString().Should().Be("0.0");
    }

    [Fact]
    public async Task SgvInMmol_RoundsAtTheBoundaryWithTheCoreFactor()
    {
        var controller = NewController("mmol", Sgv(100, 0));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("sgv").GetString().Should().Be("5.5");
    }

    [Fact]
    public async Task BgdeltaInMmol_RoundsAtTheBoundaryWithTheCoreFactor()
    {
        var controller = NewController("mmol", Sgv(209, 0), Sgv(100, 5));

        var bg = FirstBg(await Get(controller));

        bg.GetProperty("bgdelta").GetString().Should().Be("6.0");
    }
}
