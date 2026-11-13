using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The four repositories that soft delete on a tracked entity: the delete stamps <c>DeletedAt</c>
/// from the injected clock, flags a user's delete as the user's (<c>deleted_by_user</c>), and writes
/// the <c>delete</c> row of <c>mutation_audit_log</c> the V4 records write. A system delete is
/// neither flagged nor audited, so a resync may bring the record back.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TrackedSoftDeleteAuditTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db =
        TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", new MutationAuditInterceptor());
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> Records => new() { "LabHbA1c", "SleepSession", "PatientDevice", "PatientInsulin" };

    [Theory]
    [MemberData(nameof(Records))]
    public async Task A_user_delete_stamps_the_injected_clock_flags_the_user_and_audits(string record)
    {
        var (id, entityType) = Seed(record);

        await DeleteAsync(record, id, new UserAudit());

        var state = await ReadStateAsync(record, id);
        state.DeletedAt.Should().Be(Now.UtcDateTime);
        state.DeletedByUser.Should().BeTrue();
        var log = (await ReadAuditAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("delete");
        log.EntityType.Should().Be(entityType);
        log.EntityId.Should().Be(id);
        log.ActorName.Should().Be("tester");
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task A_system_delete_is_neither_flagged_nor_audited(string record)
    {
        var (id, _) = Seed(record);

        await DeleteAsync(record, id, SystemAuditContext.ForService("connector:test"));

        var state = await ReadStateAsync(record, id);
        state.DeletedAt.Should().Be(Now.UtcDateTime);
        state.DeletedByUser.Should().BeFalse();
        (await ReadAuditAsync()).Should().BeEmpty();
    }

    private (Guid Id, string EntityType) Seed(string record)
    {
        using var db = _db.CreateContext();
        var id = Guid.CreateVersion7();
        switch (record)
        {
            case "LabHbA1c":
                db.LabHbA1cResults.Add(new LabHbA1cResultEntity
                {
                    Id = id, TenantId = TenantId, MeasuredAt = Now.UtcDateTime.Date, ValuePercent = 6.5,
                    CreatedAt = Now.UtcDateTime,
                });
                break;
            case "SleepSession":
                db.SleepSessions.Add(new SleepSessionEntity
                {
                    Id = id, TenantId = TenantId, StartTime = Now.UtcDateTime.AddHours(-8), EndTime = Now.UtcDateTime,
                    Source = "Fitbit", Type = "Overnight", DetectionMethod = "Auto", OriginalId = "night-1",
                });
                break;
            case "PatientDevice":
                db.PatientDevices.Add(new PatientDeviceEntity
                {
                    Id = id, TenantId = TenantId, DeviceCategory = nameof(DeviceCategory.InsulinPump),
                    Manufacturer = "Acme", Model = "P1",
                });
                break;
            case "PatientInsulin":
                db.PatientInsulins.Add(new PatientInsulinEntity
                {
                    Id = id, TenantId = TenantId, InsulinCategory = nameof(InsulinCategory.RapidActing), Name = "Humalog",
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(record));
        }

        db.SaveChanges();
        return (id, record switch
        {
            "LabHbA1c" => "LabHbA1cResult",
            "SleepSession" => "SleepSession",
            "PatientDevice" => "PatientDevice",
            _ => "PatientInsulin",
        });
    }

    private async Task DeleteAsync(string record, Guid id, IAuditContext actor)
    {
        using var source = _db.CreateContext().AttributedTo(actor);
        var factory = new TestNocturneDbContextFactory(source);
        switch (record)
        {
            case "LabHbA1c":
                (await new LabHbA1cResultRepository(factory, _clock).DeleteAsync(id)).Should().BeTrue();
                break;
            case "SleepSession":
                (await new SleepSessionRepository(factory, _clock).DeleteSessionAsync(id)).Should().BeTrue();
                break;
            case "PatientDevice":
                await new PatientDeviceRepository(factory, NullLogger<PatientDeviceRepository>.Instance, _clock)
                    .DeleteAsync(id, WriteOrigin.Live);
                break;
            default:
                await new PatientInsulinRepository(factory, NullLogger<PatientInsulinRepository>.Instance, _clock)
                    .DeleteAsync(id, WriteOrigin.Live);
                break;
        }
    }

    private async Task<(DateTime? DeletedAt, bool DeletedByUser)> ReadStateAsync(string record, Guid id)
    {
        await using var db = _db.CreateContext();
        ISoftDeletable row = record switch
        {
            "LabHbA1c" => await db.LabHbA1cResults.IgnoreQueryFilters().SingleAsync(e => e.Id == id),
            "SleepSession" => await db.SleepSessions.IgnoreQueryFilters().SingleAsync(e => e.Id == id),
            "PatientDevice" => await db.PatientDevices.IgnoreQueryFilters().SingleAsync(e => e.Id == id),
            _ => await db.PatientInsulins.IgnoreQueryFilters().SingleAsync(e => e.Id == id),
        };
        return (row.DeletedAt, (bool)db.Entry(row).Property("DeletedByUser").CurrentValue!);
    }

    private async Task<List<MutationAuditLogEntity>> ReadAuditAsync()
    {
        await using var db = _db.CreateContext();
        return await db.Set<MutationAuditLogEntity>().ToListAsync();
    }

    private sealed class UserAudit : IAuditContext
    {
        public Guid? SubjectId => Guid.CreateVersion7();
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/record";
        public bool IsSystem => false;
    }
}
