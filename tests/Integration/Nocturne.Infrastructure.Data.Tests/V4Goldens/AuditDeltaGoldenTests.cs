using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the soft-delete-on-<c>DeleteByLegacyIdAsync</c> audit behaviour the
/// V4RepositoryBase refactor NORMALIZED (delta D5). The base's <c>DeleteByLegacyIdAsync</c> now
/// routes through the audited soft-delete helper, so EVERY V4 type writes a
/// <see cref="MutationAuditLogEntity"/> row on a legacy-id delete — not just the dedup participants
/// that used to override it. Both a formerly-RAW type (BGCheck, which inherited the plain base) and
/// an already-AUDITED type (DeviceEvent) are pinned, under both attributions:
///   - deleted by a human actor → audit row present (the D5 delta, and the row the audit trail is for);
///   - deleted by a connector/background sweep → no audit row, because the helper applies the same
///     system skip as the <c>MutationAuditInterceptor</c>. A sweep has no actor and its provenance
///     is already on the record (data_source); recording it grew mutation_audit_log to 24GB.
/// The <c>MutationAuditInterceptor</c> never contributes here: the helper detaches the entities and
/// issues the soft-delete as a bulk update, so every row below comes from the helper itself.
/// <para>
/// Delta D8 is D5's twin for <c>DeleteBySyncIdentifierAsync</c>: hoisting it onto
/// <c>SyncKeyedRepositoryBase</c> put every keyed delete on the broadcasting helper, which has to
/// materialize the matched rows and therefore audits each one instead of writing the count-only
/// <c>bulk_delete</c> summary the non-broadcasting helper wrote. Same system skip.
/// </para>
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

    /// <summary>
    /// Attributes the scope's writes to a human actor, as the API's audit middleware does for a
    /// request. Without this a golden runs under the fixture's default system attribution.
    /// </summary>
    private static void AttributeScopeToUser(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<TestAuditContext>().AttributeToUser(Guid.NewGuid());

    [Fact]
    public async Task D5_FormerlyRawType_BGCheck_DeleteByLegacyId_WritesAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        AttributeScopeToUser(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IBGCheckRepository>();

        var created = await repo.CreateAsync(
            new BGCheck { Timestamp = T0, Glucose = 95, DataSource = "manual", LegacyId = "bg-del" }, WriteOrigin.Live,
            CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("bg-del", WriteOrigin.Live, CancellationToken.None);
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
        AttributeScopeToUser(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();

        var created = await repo.CreateAsync(
            new DeviceEvent { Timestamp = T0, EventType = DeviceEventType.SiteChange, DataSource = "aaps", LegacyId = "de-del" }, WriteOrigin.Live,
            CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("de-del", WriteOrigin.Live, CancellationToken.None);
        deleted.Should().Be(1);

        // An AUDITED type wrote an audit row before D5 and still does after (unchanged).
        (await AuditRowCountAsync(tenant, "DeviceEvent", created.Id)).Should().Be(1);
    }

    [Fact]
    public async Task D5_FormerlyRawType_BGCheck_SystemDeleteByLegacyId_WritesNoAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        // No AttributeScopeToUser: the fixture's default is system attribution, as a connector
        // resync sweep carries.
        var repo = scope.ServiceProvider.GetRequiredService<IBGCheckRepository>();

        var created = await repo.CreateAsync(
            new BGCheck { Timestamp = T0, Glucose = 95, DataSource = "nightscout", LegacyId = "bg-sys-del" },
            WriteOrigin.Live, CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("bg-sys-del", WriteOrigin.Live, CancellationToken.None);
        deleted.Should().Be(1, "the sweep still soft-deletes the row");

        (await AuditRowCountAsync(tenant, "BGCheck", created.Id)).Should().Be(0);
    }

    [Fact]
    public async Task D5_AuditedType_DeviceEvent_SystemDeleteByLegacyId_WritesNoAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();

        var created = await repo.CreateAsync(
            new DeviceEvent { Timestamp = T0, EventType = DeviceEventType.SiteChange, DataSource = "nightscout", LegacyId = "de-sys-del" },
            WriteOrigin.Live, CancellationToken.None);

        var deleted = await repo.DeleteByLegacyIdAsync("de-sys-del", WriteOrigin.Live, CancellationToken.None);
        deleted.Should().Be(1, "the sweep still soft-deletes the row");

        (await AuditRowCountAsync(tenant, "DeviceEvent", created.Id)).Should().Be(0);
    }

    [Fact]
    public async Task D8_DeviceEvent_DeleteBySyncIdentifier_WritesPerRecordAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        AttributeScopeToUser(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();

        var created = await repo.CreateAsync(
            new DeviceEvent { Timestamp = T0, EventType = DeviceEventType.SiteChange, DataSource = "aaps", SyncIdentifier = "de-sync-del" },
            WriteOrigin.Live, CancellationToken.None);

        var deleted = await repo.DeleteBySyncIdentifierAsync("aaps", "de-sync-del", WriteOrigin.Live, CancellationToken.None);
        deleted.Should().Be(1);

        // D8 re-baseline (was one count-only bulk_delete summary row, and none carrying the record id).
        (await AuditRowCountAsync(tenant, "DeviceEvent", created.Id)).Should().Be(1);
    }

    [Fact]
    public async Task D8_DeviceEvent_SystemDeleteBySyncIdentifier_WritesNoAuditRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();

        var created = await repo.CreateAsync(
            new DeviceEvent { Timestamp = T0, EventType = DeviceEventType.SiteChange, DataSource = "nightscout", SyncIdentifier = "de-sys-sync-del" },
            WriteOrigin.Live, CancellationToken.None);

        var deleted = await repo.DeleteBySyncIdentifierAsync("nightscout", "de-sys-sync-del", WriteOrigin.Live, CancellationToken.None);
        deleted.Should().Be(1, "the sweep still soft-deletes the row");

        (await AuditRowCountAsync(tenant, "DeviceEvent", created.Id)).Should().Be(0);
    }
}
