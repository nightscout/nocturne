using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

[Trait("Category", "Unit")]
public class SignalRSensorGlucoseEventSinkTests
{
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();

    private SignalRSensorGlucoseEventSink CreateSink()
    {
        _tenantAccessor
            .SetupGet(t => t.Context)
            .Returns(new TenantContext(Guid.NewGuid(), "rhys", "rhys", true));

        return new SignalRSensorGlucoseEventSink(
            _sideEffects.Object,
            _tenantAccessor.Object,
            NullLogger<SignalRSensorGlucoseEventSink>.Instance);
    }

    [Fact]
    public async Task OnCreatedAsync_BroadcastsMappedEntries_OnTheEntriesCollection()
    {
        // Arrange
        IReadOnlyList<Entry>? broadcast = null;
        string? collection = null;
        _sideEffects
            .Setup(s => s.OnCreatedAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Entry>>(),
                It.IsAny<WriteEffectOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<Entry>, WriteEffectOptions?, CancellationToken>(
                (col, recs, _, _) => { collection = col; broadcast = recs; })
            .Returns(Task.CompletedTask);

        var reading = new SensorGlucose
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Mgdl = 120,
        };

        var sink = CreateSink();

        // Act
        await sink.OnCreatedAsync(new[] { reading });

        // Assert — V4 glucose must surface on the real-time "entries" collection in Entry shape.
        collection.Should().Be("entries");
        broadcast.Should().ContainSingle();
        broadcast![0].Type.Should().Be("sgv");
        broadcast[0].Sgv.Should().Be(120);
        broadcast[0].Id.Should().Be(reading.Id.ToString());
    }

    [Fact]
    public async Task OnUpdatedAsync_BroadcastsMappedEntry_OnTheEntriesCollection()
    {
        // Arrange
        Entry? broadcast = null;
        string? collection = null;
        WriteEffectOptions? options = null;
        _sideEffects
            .Setup(s => s.OnUpdatedAsync(
                It.IsAny<string>(),
                It.IsAny<Entry>(),
                It.IsAny<WriteEffectOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Entry, WriteEffectOptions?, CancellationToken>(
                (col, rec, opts, _) => { collection = col; broadcast = rec; options = opts; })
            .Returns(Task.CompletedTask);

        var reading = new SensorGlucose
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Mgdl = 95,
        };

        var sink = CreateSink();

        // Act
        await sink.OnUpdatedAsync(reading);

        // Assert — an edited V4 glucose reading must surface as an "entries" update in Entry shape.
        collection.Should().Be("entries");
        broadcast.Should().NotBeNull();
        broadcast!.Type.Should().Be("sgv");
        broadcast.Sgv.Should().Be(95);
        broadcast.Id.Should().Be(reading.Id.ToString());
        // Parity with SignalREntryEventSink: updates pass BroadcastDataUpdate=false (the update path emits no dataUpdate anyway).
        options.Should().NotBeNull();
        options!.BroadcastDataUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task OnCreatedAsync_DoesNothing_ForEmptyBatch()
    {
        var sink = CreateSink();

        await sink.OnCreatedAsync(Array.Empty<SensorGlucose>());

        _sideEffects.Verify(
            s => s.OnCreatedAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Entry>>(),
                It.IsAny<WriteEffectOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
