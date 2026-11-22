using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Caching;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The first arrivals are read once per tenant and kept, read again when a reading names a
/// connection the kept map does not hold, and dropped when glucose is deleted, restored or changed.
/// </summary>
[Trait("Category", "Unit")]
public class ImportedHistoryTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly ITenantCache _cache = TestTenantCache.Create();
    private List<GlucoseStreamArrival> _streams = [new(null, "xdrip", null, T0)];

    public ImportedHistoryTests()
    {
        _glucose.Setup(g => g.GetStreamFirstArrivalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _streams);
    }

    private ImportedHistory Sut() => new(_glucose.Object, Mock.Of<IOAuthGrantRepository>(), _cache);

    private void Window(params GlucoseStreamReading[] readings) =>
        _glucose.Setup(g => g.GetStreamReadingsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

    [Fact]
    public async Task The_map_is_read_once_and_again_for_a_connection_it_does_not_hold()
    {
        var early = new GlucoseStreamReading(Guid.NewGuid(), T0.AddMinutes(-5), null, "xdrip", null);
        Window(early);
        (await Sut().InAsync(T0.AddDays(-1), T0.AddDays(1), default)).Should().BeEquivalentTo([early.Id]);
        (await Sut().InAsync(T0.AddDays(-1), T0.AddDays(1), default)).Should().BeEquivalentTo([early.Id]);
        _glucose.Verify(g => g.GetStreamFirstArrivalsAsync(It.IsAny<CancellationToken>()), Times.Once);

        _streams = [.. _streams, new(null, "dexcom", null, T0.AddHours(2))];
        var newConnection = new GlucoseStreamReading(Guid.NewGuid(), T0.AddHours(1), null, "dexcom", null);
        Window(early, newConnection);

        (await Sut().InAsync(T0.AddDays(-1), T0.AddDays(1), default)).Should().BeEquivalentTo([early.Id, newConnection.Id]);
        _glucose.Verify(g => g.GetStreamFirstArrivalsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// A restore is announced as a creation of a row stored long before the write, and can move a
    /// first arrival earlier, so it drops the map; a reading stored by the write itself does not.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public async Task A_glucose_delete_change_or_restore_drops_the_map_and_a_creation_keeps_it(
        bool deleted, bool updated, bool restored, bool drops)
    {
        var now = T0.AddDays(1);
        Window(new GlucoseStreamReading(Guid.NewGuid(), T0, null, "xdrip", null));
        await Sut().InAsync(T0.AddDays(-1), T0.AddDays(1), default);
        IV4Record reading = new SensorGlucose
        {
            Id = Guid.NewGuid(), Timestamp = T0, CreatedAt = restored ? T0.AddDays(-3) : now.AddSeconds(-1),
        };
        var change = new RecordTypeChange(
            typeof(SensorGlucose), WriteOrigin.Live, [reading], updated ? [reading] : [], deleted ? [reading] : []);

        await new GlucoseFirstArrivalsInvalidation(_cache, new FakeTimeProvider(new DateTimeOffset(now)))
            .OnChangedAsync(new RecordsChanged([change]), default);
        await Sut().InAsync(T0.AddDays(-1), T0.AddDays(1), default);

        _glucose.Verify(g => g.GetStreamFirstArrivalsAsync(It.IsAny<CancellationToken>()), Times.Exactly(drops ? 2 : 1));
    }
}
