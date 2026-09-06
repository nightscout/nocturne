using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SleepSessionRepositoryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly NocturneDbContext _context;
    private readonly SleepSessionRepository _repository;

    public SleepSessionRepositoryTests()
    {
        var dbName = $"sleep_session_tests_{Guid.NewGuid()}";
        _context = TestDbContextFactory.CreateInMemoryContext(dbName);
        _context.TenantId = TenantA;
        _repository = new SleepSessionRepository(new TestTenantDbContextFactory(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private SleepSessionEntity CreateEntity(
        Guid tenantId,
        DateTime startTime,
        DateTime endTime,
        string source = "Fitbit",
        string type = "Overnight",
        string? originalId = null)
    {
        return new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            StartTime = startTime,
            EndTime = endTime,
            Source = source,
            Type = type,
            DetectionMethod = "Auto",
            DurationMs = (long)(endTime - startTime).TotalMilliseconds,
            TotalSleepMs = (long)(endTime - startTime).TotalMilliseconds,
            OriginalId = originalId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private async Task SeedAsync(params SleepSessionEntity[] entities)
    {
        _context.SleepSessions.AddRange(entities);
        await _context.SaveChangesAsync();
    }

    // --- GetSessionsAsync ---

    [Fact]
    public async Task GetSessionsAsync_returns_empty_when_no_data()
    {
        var result = await _repository.GetSessionsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionsAsync_filters_by_date_range()
    {
        // Session that spans 22:00 Jan 1 to 06:00 Jan 2
        var session1 = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        // Session that spans 22:00 Jan 2 to 06:00 Jan 3
        var session2 = CreateEntity(TenantA,
            new DateTime(2026, 1, 2, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 3, 6, 0, 0, DateTimeKind.Utc));

        // Session that spans 22:00 Jan 3 to 06:00 Jan 4
        var session3 = CreateEntity(TenantA,
            new DateTime(2026, 1, 3, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 4, 6, 0, 0, DateTimeKind.Utc));

        await SeedAsync(session1, session2, session3);

        // Query for sessions overlapping Jan 2
        var from = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 23, 59, 59, DateTimeKind.Utc);
        var result = (await _repository.GetSessionsAsync(from: from, to: to)).ToList();

        // session1 ends Jan 2 06:00 (EndTime >= from) and starts Jan 1 22:00 (StartTime <= to) => included
        // session2 starts Jan 2 22:00 (StartTime <= to) and ends Jan 3 06:00 (EndTime >= from) => included
        // session3 starts Jan 3 22:00 (StartTime > to) => excluded
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSessionsAsync_respects_tenant_isolation()
    {
        var ourSession = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        var theirSession = CreateEntity(TenantB,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        await SeedAsync(ourSession, theirSession);

        var result = (await _repository.GetSessionsAsync()).ToList();

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetSessionsAsync_filters_by_type()
    {
        var overnight = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            type: "Overnight");

        var nap = CreateEntity(TenantA,
            new DateTime(2026, 1, 2, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc),
            type: "Nap");

        await SeedAsync(overnight, nap);

        var result = (await _repository.GetSessionsAsync(type: SleepSessionType.Nap)).ToList();

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetSessionsAsync_includes_stages_when_requested()
    {
        var entity = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        entity.Stages =
        [
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                Stage = "Light",
                Ordinal = 0,
            },
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                StartTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Stage = "Deep",
                Ordinal = 1,
            },
        ];

        await SeedAsync(entity);

        var result = (await _repository.GetSessionsAsync(includeStages: true)).ToList();

        result.Should().HaveCount(1);
        result[0].Stages.Should().HaveCount(2);
        result[0].Stages![0].Stage.Should().Be(SleepStageType.Light);
        result[0].Stages[1].Stage.Should().Be(SleepStageType.Deep);
    }

    [Fact]
    public async Task GetSessionsAsync_omits_stages_by_default()
    {
        var entity = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        entity.Stages =
        [
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                Stage = "Light",
                Ordinal = 0,
            },
        ];

        await SeedAsync(entity);

        var result = (await _repository.GetSessionsAsync()).ToList();

        result.Should().HaveCount(1);
        result[0].Stages.Should().BeNull();
    }

    [Fact]
    public async Task GetSessionsAsync_filters_by_source()
    {
        var fitbit = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            source: "Fitbit");

        var oura = CreateEntity(TenantA,
            new DateTime(2026, 1, 2, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 3, 6, 0, 0, DateTimeKind.Utc),
            source: "Oura");

        await SeedAsync(fitbit, oura);

        var result = (await _repository.GetSessionsAsync(source: SleepSource.Oura)).ToList();

        result.Should().HaveCount(1);
    }

    // --- CountSessionsAsync ---

    [Fact]
    public async Task CountSessionsAsync_returns_filtered_count()
    {
        var session1 = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        var session2 = CreateEntity(TenantA,
            new DateTime(2026, 1, 2, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 3, 6, 0, 0, DateTimeKind.Utc));

        await SeedAsync(session1, session2);

        var count = await _repository.CountSessionsAsync();

        count.Should().Be(2);
    }

    // --- GetSessionByIdAsync ---

    [Fact]
    public async Task GetSessionByIdAsync_includes_stages_and_biometric_samples()
    {
        var entity = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        entity.Stages =
        [
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                Stage = "Light",
                Ordinal = 0,
            },
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                StartTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Stage = "Deep",
                Ordinal = 1,
            },
        ];

        entity.BiometricSamples =
        [
            new SleepBiometricSampleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                Timestamp = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                HeartRate = 58.0f,
            },
        ];

        await SeedAsync(entity);

        var result = await _repository.GetSessionByIdAsync(entity.Id);

        result.Should().NotBeNull();
        result!.Stages.Should().HaveCount(2);
        result.Stages![0].Stage.Should().Be(SleepStageType.Light);
        result.Stages[1].Stage.Should().Be(SleepStageType.Deep);
        result.BiometricSamples.Should().HaveCount(1);
        result.BiometricSamples![0].HeartRate.Should().Be(58.0f);
    }

    [Fact]
    public async Task GetSessionByIdAsync_returns_null_when_not_found()
    {
        var result = await _repository.GetSessionByIdAsync(Guid.CreateVersion7());

        result.Should().BeNull();
    }

    // --- UpsertSessionAsync ---

    [Fact]
    public async Task UpsertSessionAsync_creates_new_session()
    {
        var session = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 28_800_000,
            TotalSleepMs = 25_200_000,
            OriginalId = "fitbit-abc-123",
        };

        var result = await _repository.UpsertSessionAsync(session);

        result.Should().NotBeNull();
        result.OriginalId.Should().Be("fitbit-abc-123");

        var count = await _repository.CountSessionsAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertSessionAsync_replaces_on_duplicate_source_and_original_id()
    {
        var session1 = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 28_800_000,
            TotalSleepMs = 25_200_000,
            OriginalId = "fitbit-abc-123",
            SleepScore = 80,
        };

        await _repository.UpsertSessionAsync(session1);

        var session2 = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 30, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 30_600_000,
            TotalSleepMs = 27_000_000,
            OriginalId = "fitbit-abc-123",
            SleepScore = 85,
        };

        var result = await _repository.UpsertSessionAsync(session2);

        result.SleepScore.Should().Be(85);

        var count = await _repository.CountSessionsAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertSessionAsync_replaces_by_id_when_original_id_is_null()
    {
        var existing = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            originalId: "fitbit-abc-123");
        existing.SleepScore = 80;

        await SeedAsync(existing);

        var incoming = new SleepSession
        {
            Id = existing.Id.ToString(),
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 30, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 30_600_000,
            TotalSleepMs = 27_000_000,
            OriginalId = null,
            SleepScore = 85,
        };

        var result = await _repository.UpsertSessionAsync(incoming);

        result.Id.Should().Be(existing.Id.ToString());
        result.SleepScore.Should().Be(85);

        var count = await _repository.CountSessionsAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertSessionAsync_replaces_by_id_when_original_id_differs()
    {
        var existing = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            originalId: "fitbit-abc-123");

        existing.Stages =
        [
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = existing.Id,
                StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                Stage = "Light",
                Ordinal = 0,
            },
        ];

        await SeedAsync(existing);

        var incoming = new SleepSession
        {
            Id = existing.Id.ToString(),
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 28_800_000,
            TotalSleepMs = 25_200_000,
            OriginalId = "fitbit-different-999",
            Stages =
            [
                new SleepStageInterval
                {
                    StartTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                    EndTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Stage = SleepStageType.Deep,
                    Ordinal = 0,
                },
            ],
        };

        var result = await _repository.UpsertSessionAsync(incoming);

        result.Id.Should().Be(existing.Id.ToString());
        result.Stages.Should().ContainSingle().Which.Stage.Should().Be(SleepStageType.Deep);

        var count = await _repository.CountSessionsAsync();
        count.Should().Be(1);

        var persisted = await _repository.GetSessionByIdAsync(existing.Id);
        persisted!.Stages.Should().ContainSingle().Which.Stage.Should().Be(SleepStageType.Deep);
    }

    [Fact]
    public async Task UpsertSessionAsync_keeps_primary_key_stable_across_original_id_resync()
    {
        var session1 = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 28_800_000,
            OriginalId = "fitbit-abc-123",
        };

        var first = await _repository.UpsertSessionAsync(session1);

        var session2 = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 30, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 30_600_000,
            OriginalId = "fitbit-abc-123",
        };

        var second = await _repository.UpsertSessionAsync(session2);

        second.Id.Should().Be(first.Id);
    }

    // --- UpdateSessionAsync ---

    [Fact]
    public async Task UpdateSessionAsync_returns_updated_values()
    {
        var entity = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            source: "Fitbit");

        entity.SleepScore = 75;

        await SeedAsync(entity);

        var updated = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 21, 30, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 7, 0, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 34_200_000,
            TotalSleepMs = 30_000_000,
            SleepScore = 90,
        };

        var result = await _repository.UpdateSessionAsync(entity.Id, updated);

        result.Should().NotBeNull();
        result!.Id.Should().Be(entity.Id.ToString());
        result.SleepScore.Should().Be(90);
        result.StartTime.Should().Be(new DateTime(2026, 1, 1, 21, 30, 0, DateTimeKind.Utc));
        result.EndTime.Should().Be(new DateTime(2026, 1, 2, 7, 0, 0, DateTimeKind.Utc));

        var count = await _repository.CountSessionsAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpdateSessionAsync_returns_null_when_not_found()
    {
        var session = new SleepSession
        {
            StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Fitbit,
            DurationMs = 28_800_000,
        };

        var result = await _repository.UpdateSessionAsync(Guid.CreateVersion7(), session);

        result.Should().BeNull();
    }

    // --- DeleteSessionAsync ---

    [Fact]
    public async Task DeleteSessionAsync_returns_false_when_not_found()
    {
        var result = await _repository.DeleteSessionAsync(Guid.CreateVersion7());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSessionAsync_removes_session_and_children()
    {
        var entity = CreateEntity(TenantA,
            new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc));

        entity.Stages =
        [
            new SleepStageEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                SleepSessionId = entity.Id,
                StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc),
                Stage = "Light",
                Ordinal = 0,
            },
        ];

        await SeedAsync(entity);

        var result = await _repository.DeleteSessionAsync(entity.Id);

        result.Should().BeTrue();

        var count = await _repository.CountSessionsAsync();
        count.Should().Be(0);
    }
}
