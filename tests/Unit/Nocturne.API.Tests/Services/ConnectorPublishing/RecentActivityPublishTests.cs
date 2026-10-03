using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// The recent path a catch-up hands activity below its cursor to. An activity is stored as a state
/// span, heart rate, step count or sleep session under its id, and any of them holding the id leaves
/// the activity as stored.
/// </summary>
[Trait("Category", "Unit")]
public class RecentActivityPublishTests : IDisposable
{
    private const string Source = "nightscout-connector";
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly Mock<IActivityService> _activities = new();
    private readonly List<Activity> _created = [];

    public RecentActivityPublishTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantId;
        _activities
            .Setup(s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Activity>, CancellationToken>((batch, _) => _created.AddRange(batch))
            .ReturnsAsync((IEnumerable<Activity> batch, CancellationToken _) => batch);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Only_activities_nothing_stored_holds_are_written()
    {
        var now = DateTime.UtcNow;
        _context.StateSpans.Add(Span("act-span", deletedAt: null));
        _context.StateSpans.Add(Span("act-user-deleted", deletedAt: now));
        _context.StateSpans.Add(Span("act-swept", deletedAt: now));
        _context.HeartRates.Add(new HeartRateEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "act-hr", Timestamp = now, Bpm = 70 });
        _context.StepCounts.Add(new StepCountEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "act-steps", Timestamp = now, Metric = 500 });
        _context.SleepSessions.Add(new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "act-sleep", Source = ActivityStateSpanMapper.SleepSessionSource,
            StartTime = now.AddHours(-8), EndTime = now, Type = "sleep", DetectionMethod = "manual",
        });
        _context.SleepSessions.Add(new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "act-sleep-deleted", Source = ActivityStateSpanMapper.SleepSessionSource,
            StartTime = now.AddHours(-8), EndTime = now, Type = "sleep", DetectionMethod = "manual", DeletedAt = now,
        });
        await _context.SaveChangesAsync();
        MarkDeletedByUser(_context.StateSpans.IgnoreQueryFilters().Single(s => s.OriginalId == "act-user-deleted"));
        MarkDeletedByUser(_context.SleepSessions.IgnoreQueryFilters().Single(s => s.OriginalId == "act-sleep-deleted"));

        var written = await Publisher().PublishRecentActivityAsync(
            [
                Activity("act-span"), Activity("act-user-deleted"), Activity("act-swept"),
                Activity("act-hr"), Activity("act-steps"), Activity("act-sleep"), Activity("act-sleep-deleted"),
                Activity("act-late"), Activity(null),
            ],
            Source, WriteOrigin.Live);

        written.Should().Be(2);
        _created.Select(a => a.Id).Should().BeEquivalentTo(["act-swept", "act-late"],
            "a live row or a user tombstone holds the id; a system sweep does not");
        _created.Should().OnlyContain(a => a.DataSource == Source);
    }

    [Fact]
    public async Task Another_tenants_row_does_not_hold_the_id()
    {
        var other = Span("act-shared-id", deletedAt: null);
        other.TenantId = Guid.NewGuid();
        _context.StateSpans.Add(other);
        await _context.SaveChangesAsync();

        var written = await Publisher().PublishRecentActivityAsync([Activity("act-shared-id")], Source, WriteOrigin.Live);

        written.Should().Be(1);
        _created.Select(a => a.Id).Should().Equal("act-shared-id");
    }

    [Fact]
    public async Task Another_sources_sleep_session_does_not_hold_the_id()
    {
        var now = DateTime.UtcNow;
        _context.SleepSessions.Add(new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, OriginalId = "act-shared-sleep-id", Source = "Apple",
            StartTime = now.AddHours(-8), EndTime = now, Type = "sleep", DetectionMethod = "manual",
        });
        await _context.SaveChangesAsync();

        var written = await Publisher().PublishRecentActivityAsync([Activity("act-shared-sleep-id")], Source, WriteOrigin.Live);

        written.Should().Be(1, "an activity's sleep session is keyed by its own source and the id");
        _created.Select(a => a.Id).Should().Equal("act-shared-sleep-id");
    }

    [Fact]
    public async Task Nothing_unheld_writes_nothing()
    {
        _context.StateSpans.Add(Span("act-span", deletedAt: null));
        await _context.SaveChangesAsync();

        var written = await Publisher().PublishRecentActivityAsync([Activity("act-span")], Source, WriteOrigin.Live);

        written.Should().Be(0);
        _activities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_failed_lookup_writes_nothing_and_reports_the_failure()
    {
        var publisher = Publisher();
        _context.Dispose();

        var written = await publisher.PublishRecentActivityAsync([Activity("act-late")], Source, WriteOrigin.Live);

        written.Should().BeNull();
        _activities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reports_what_the_write_stored_not_what_it_was_handed()
    {
        _activities
            .Setup(s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Activity> batch, CancellationToken _) => batch.Take(1).ToList());

        var written = await Publisher().PublishRecentActivityAsync(
            [Activity("act-late-1"), Activity("act-late-2"), Activity("act-late-3")], Source, WriteOrigin.Live);

        written.Should().Be(1, "the other two were skipped by the write, e.g. as user-deleted");
    }

    [Fact]
    public async Task A_failed_write_reports_the_failure()
    {
        _activities
            .Setup(s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic"));

        var written = await Publisher().PublishRecentActivityAsync([Activity("act-late")], Source, WriteOrigin.Live);

        written.Should().BeNull();
    }

    private void MarkDeletedByUser(object row)
    {
        _context.Entry(row).Property("DeletedByUser").CurrentValue = true;
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private static StateSpanEntity Span(string originalId, DateTime? deletedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = TenantId,
        Category = "Exercise",
        State = "exercise",
        StartTimestamp = DateTime.UtcNow.AddHours(-3),
        Source = Source,
        OriginalId = originalId,
        DeletedAt = deletedAt,
    };

    private static Activity Activity(string? id) =>
        new() { Id = id, Type = "exercise", CreatedAt = "2026-03-01T09:00:00.000Z", Duration = 30 };

    private MetadataPublisher Publisher() => new(
        Mock.Of<IProfileWriteService>(),
        Mock.Of<IFoodService>(),
        Mock.Of<IConnectorFoodEntryService>(),
        _activities.Object,
        Mock.Of<IStateSpanService>(),
        Mock.Of<ISystemEventRepository>(),
        Mock.Of<INoteRepository>(),
        Mock.Of<IBodyWeightService>(),
        Mock.Of<IStepCountService>(),
        Mock.Of<IHeartRateService>(),
        Mock.Of<ITenantOwnerResolver>(),
        Mock.Of<ITenantAccessor>(),
        _context,
        Mock.Of<IAuditContext>(),
        new PublishSkipTally(),
        NullLogger<MetadataPublisher>.Instance);
}
