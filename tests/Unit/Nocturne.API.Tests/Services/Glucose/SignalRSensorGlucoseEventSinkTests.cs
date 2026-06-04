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
