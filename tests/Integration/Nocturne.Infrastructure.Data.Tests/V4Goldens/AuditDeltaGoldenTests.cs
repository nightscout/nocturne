using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the soft-delete-on-<c>DeleteByLegacyIdAsync</c> audit behaviour the
/// V4RepositoryBase refactor NORMALIZED (delta D5). The base's <c>DeleteByLegacyIdAsync</c> now
/// routes through the audited soft-delete helper, so EVERY V4 type writes a
/// <see cref="MutationAuditLogEntity"/> row on a legacy-id delete — not just the dedup participants
/// that used to override it. The two scenarios below pin both sides post-normalization:
///   - a formerly-RAW type (BGCheck, which inherited the plain base) → audit row present (the D5 delta);
///   - an already-AUDITED type (DeviceEvent) → audit row present (unchanged).
/// The <see cref="V4GoldenFixture"/>'s <c>SystemAuditContext</c> short-circuits the
/// <c>MutationAuditInterceptor</c> (IsSystem == true), so the only audit rows that can appear here
/// come from the audited soft-delete helper, which writes them directly.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class AuditDeltaGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public AuditDeltaGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private Task<int> AuditRowCountAsync(Guid tenant, string entityType, Guid entityId) =>
        _fx.QueryAsync(tenant, ctx => ctx.Set<MutationAuditLogEntity>().AsNoTracking()
            .CountAsync(a => a.EntityType == entityType && a.EntityId == entityId && a.Action == "delete"));

    [Fact]
    public async Task D5_FormerlyRawType_BGCheck_DeleteByLegacyId_WritesAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBGCheckRepository>();

        var created = await repo.CreateAsync(
            new BGCheck { Timestamp = T0, Glucose = 95, DataSource = "manual", LegacyId = "bg-del" },
            CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("bg-del", CancellationToken.None);
        deleted.Should().Be(1);

        // D5 re-baseline (was 0): the base DeleteByLegacyIdAsync now routes through the audited
        // soft-delete helper, so a formerly-raw type writes a mutation_audit_log row too.
        (await AuditRowCountAsync(tenant, "BGCheck", created.Id)).Should().Be(1);
    }

    [Fact]
    public async Task D5_AuditedType_DeviceEvent_DeleteByLegacyId_WritesAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();

        var created = await repo.CreateAsync(
            new DeviceEvent { Timestamp = T0, EventType = DeviceEventType.SiteChange, DataSource = "aaps", LegacyId = "de-del" },
            CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("de-del", CancellationToken.None);
        deleted.Should().Be(1);

        // An AUDITED type wrote an audit row before D5 and still does after (unchanged).
        (await AuditRowCountAsync(tenant, "DeviceEvent", created.Id)).Should().Be(1);
    }
}
