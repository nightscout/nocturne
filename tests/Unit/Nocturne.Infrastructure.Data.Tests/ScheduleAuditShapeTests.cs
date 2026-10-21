using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The audit row an ISF or target schedule update writes, in the shape migration
/// <c>RepairDoubleConvertedMmolProfiles</c> reads as evidence of a conversion: the entity type without
/// its <c>Entity</c> suffix, the trace id, and <c>changes.EntriesJson</c> holding the old and new entries
/// as JSON strings.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleAuditShapeTests : IDisposable
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public ScheduleAuditShapeTests()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns((HttpContext)null!);
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantId, "test", new MutationAuditInterceptor());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ASensitivityScheduleUpdate_RecordsItsOldAndNewEntriesAsJsonStrings_UnderTheTraceId()
    {
        var id = Guid.CreateVersion7();
        const string before = """[{"Time":"00:00","Value":63,"TimeAsSeconds":0}]""";
        const string after = """[{"Time":"00:00","Value":1135,"TimeAsSeconds":0}]""";
        await using (var ctx = _db.CreateContext())
        {
            ctx.SensitivitySchedules.Add(new SensitivityScheduleEntity
            {
                Id = id, TenantId = _tenantId, ProfileName = "AAPS", EntriesJson = before,
                Timestamp = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            ctx.AttributedTo(new UserAuditContext());
            (await ctx.SensitivitySchedules.SingleAsync(s => s.Id == id)).EntriesJson = after;
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var log = await verify.MutationAuditLog.SingleAsync(l => l.Action == "update");
        log.EntityType.Should().Be("SensitivitySchedule");
        log.EntityId.Should().Be(id);
        log.TraceId.Should().Be("trace-1");
        using var changes = JsonDocument.Parse(log.ChangesJson!);
        var entries = changes.RootElement.GetProperty("EntriesJson");
        entries.GetProperty("old").GetString().Should().Be(before);
        entries.GetProperty("new").GetString().Should().Be(after);
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "ApiSecret";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => "trace-1";
        public string? Endpoint => "POST /api/v1/profile";
        public bool IsSystem => false;
    }
}
