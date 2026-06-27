using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the soft-delete-on-<c>DeleteByLegacyIdAsync</c> audit behaviour the
/// V4RepositoryBase refactor NORMALIZES (delta D5). Today the base's <c>DeleteByLegacyIdAsync</c>
/// is a plain <c>ExecuteUpdateAsync</c> that bypasses the change tracker, so it writes NO
/// <see cref="MutationAuditLogEntity"/> row — only the six dedup participants that override it with
/// the audited helper produce one. The two scenarios below pin both sides of that split so D5 lands
/// as a deliberate, visible re-baseline:
///   - a RAW type (BGCheck inherits the plain base) → NO audit row;
///   - an AUDITED type (DeviceEvent overrides with the audited helper) → audit row present.
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
    public async Task D5_RawType_BGCheck_DeleteByLegacyId_DoesNotWriteAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBGCheckRepository>();

        var created = await repo.CreateAsync(
            new BGCheck { Timestamp = T0, Glucose = 95, DataSource = "manual", LegacyId = "bg-del" },
            CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("bg-del", CancellationToken.None);
        deleted.Should().Be(1);

        // Pre-D5 baseline: a RAW type inherits the plain base DeleteByLegacyIdAsync (ExecuteUpdate,
        // change-tracker-bypassing), so NO mutation_audit_log row is written.
        (await AuditRowCountAsync(tenant, "BGCheck", created.Id)).Should().Be(0);
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
