using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// Covers the real backfill low-water mark persistence in <see cref="MetadataPublisher"/>:
/// the source→connector-configuration lookup (a <c>nightscout-connector</c> source must find
/// the <c>nightscout</c> row), the JSON round-trip (no timezone shift), per-collection
/// independence, remove-on-null, and the missing-configuration no-op.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataPublisherBackfillMarkTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private readonly NocturneDbContext _db;
    private readonly MetadataPublisher _publisher;

    public MetadataPublisherBackfillMarkTests()
    {
        _db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"backfill-marks-{Guid.NewGuid():N}").Options)
        {
            TenantId = TenantId,
        };

        // Configuration rows store the bare, lowercased connector name.
        _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            ConnectorName = "nightscout",
        });
        _db.SaveChanges();

        _publisher = new MetadataPublisher(
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IFoodService>(),
            Mock.Of<IConnectorFoodEntryService>(),
            Mock.Of<IActivityService>(),
            Mock.Of<IStateSpanService>(),
            Mock.Of<ISystemEventRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<ITenantOwnerResolver>(),
            Mock.Of<ITenantAccessor>(),
            _db,
            NullLogger<MetadataPublisher>.Instance);
    }

    [Fact]
    public async Task Marks_RoundTrip_ThroughTheConnectorSourceName()
    {
        var mark = new DateTime(2024, 6, 1, 12, 30, 15, DateTimeKind.Utc);

        await _publisher.SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", mark);

        var read = await _publisher.GetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose");
        read.Should().Be(mark, "the value must round-trip without a timezone shift");
        read!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Marks_AreIndependentPerCollection()
    {
        var glucoseMark = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var treatmentsMark = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await _publisher.SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", glucoseMark);
        await _publisher.SetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments", treatmentsMark);
        await _publisher.SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", null);

        (await _publisher.GetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose")).Should().BeNull();
        (await _publisher.GetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments")).Should().Be(treatmentsMark);
    }

    [Fact]
    public async Task ClearingTheLastMark_EmptiesTheColumn()
    {
        await _publisher.SetBackfillLowWaterMarkAsync(
            "nightscout-connector", "Glucose", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await _publisher.SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", null);

        _db.ChangeTracker.Clear();
        _db.ConnectorConfigurations.Single().BackfillLowWaterMarks.Should().BeNull();
    }

    [Fact]
    public async Task UnknownSource_IsANoOp_NotAThrow()
    {
        await _publisher.SetBackfillLowWaterMarkAsync(
            "dexcom-connector", "Glucose", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        (await _publisher.GetBackfillLowWaterMarkAsync("dexcom-connector", "Glucose")).Should().BeNull();
        _db.ChangeTracker.Clear();
        _db.ConnectorConfigurations.Single().BackfillLowWaterMarks.Should().BeNull(
            "a mark for an unconfigured connector must not land on another connector's row");
    }
}
