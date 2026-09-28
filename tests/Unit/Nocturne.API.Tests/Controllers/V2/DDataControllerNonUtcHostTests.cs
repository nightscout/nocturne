using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V2;
using Nocturne.API.Tests.Services.Legacy;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V2;

/// <summary>
/// An explicit offset must survive on a host that is not on UTC, and the CI runner is.
/// </summary>
[Collection(NonUtcHostCollection.Name)]
[Trait("Category", "Unit")]
public sealed class DDataControllerNonUtcHostTests : IDisposable
{
    private const string OffsetTimestamp = "2026-09-26T08:00:00+05:00";

    private static readonly long ExpectedMills =
        new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly string? _previousTz = Environment.GetEnvironmentVariable("TZ");
    private readonly Mock<IDDataService> _service = new();
    private readonly DDataController _controller;

    public DDataControllerNonUtcHostTests()
    {
        Environment.SetEnvironmentVariable("TZ", "America/New_York");
        TimeZoneInfo.ClearCachedData();
        Assert.NotEqual(TimeSpan.Zero, TimeZoneInfo.Local.BaseUtcOffset);

        _controller = new DDataController(_service.Object, NullLogger<DDataController>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TZ", _previousTz);
        TimeZoneInfo.ClearCachedData();
    }

    [Fact]
    public async Task GetDDataAt_OffsetTimestamp_QueriesThatInstant()
    {
        _service
            .Setup(x => x.GetDDataWithRecentStatusesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DDataResponse());

        await _controller.GetDDataAt(OffsetTimestamp, CancellationToken.None);

        _service.Verify(x => x.GetDDataWithRecentStatusesAsync(ExpectedMills, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task GetRawDData_OffsetTimestamp_QueriesThatInstant()
    {
        _service
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData());

        await _controller.GetRawDData(OffsetTimestamp, CancellationToken.None);

        _service.Verify(x => x.GetDDataAsync(ExpectedMills, It.IsAny<CancellationToken>()));
    }
}
