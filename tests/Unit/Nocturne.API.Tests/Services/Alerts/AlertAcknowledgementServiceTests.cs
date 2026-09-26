using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Mocks;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertAcknowledgementServiceTests
{
    private readonly string _databaseName = $"acknowledgement_tests_{Guid.NewGuid()}";
    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<ITenantMemberService> _members = new();
    private readonly AlertAcknowledgementService _service;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ITenantAccessor _tenantAccessor;

    public AlertAcknowledgementServiceTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }
        _factory = new TestDbContextFactory(_options) { TenantOverride = _tenantId };

        var tenantAccessor = MockTenantAccessor.Create(_tenantId);

        _tenantAccessor = tenantAccessor.Object;

        _service = new AlertAcknowledgementService(
            _factory,
            _tenantAccessor,
            _broadcast.Object,
            _members.Object,
            NullLogger<AlertAcknowledgementService>.Instance);
    }

    /// <summary>
    /// Returns a context with query filters disabled — convenient for seed/assert paths
    /// that work across tenants. The service-under-test uses the real factory which sets
    /// <c>TenantId</c> per-test, so filters are intact during the actual call.
    /// </summary>
    private NocturneDbContext NewUnfilteredContext()
    {
        var ctx = new NocturneDbContext(_options);
        ctx.TenantId = _tenantId;
        return ctx;
    }

    private async Task<(Guid excursionId, Guid instanceId)> SeedActiveExcursionAsync(
        Guid? tenantId = null,
        DateTime? endedAt = null,
        DateTime? acknowledgedAt = null)
    {
        var t = tenantId ?? _tenantId;
        await using var db = new NocturneDbContext(_options);
        db.TenantId = t;
        var ruleId = Guid.NewGuid();
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = t,
            AlertRuleId = ruleId,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = endedAt,
            AcknowledgedAt = acknowledgedAt,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = t,
            AlertExcursionId = excursion.Id,
            Status = "triggered",
            TriggeredAt = excursion.StartedAt,
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return (excursion.Id, instance.Id);
    }

    // ---- AcknowledgeExcursionAsync ----

    [Fact]
    public async Task AcknowledgeExcursion_OpenExcursion_StampsAckAndSilencesInstances()
    {
        var (excursionId, instanceId) = await SeedActiveExcursionAsync();

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "system:auto-ack-on-trigger", AlertAcknowledgementAuthority.System, broadcast: true, CancellationToken.None);

        await using var db = NewUnfilteredContext();
        var excursion = await db.AlertExcursions.IgnoreQueryFilters()
            .FirstAsync(e => e.Id == excursionId);
        excursion.AcknowledgedAt.Should().NotBeNull();
        excursion.AcknowledgedBy.Should().Be("system:auto-ack-on-trigger");

        var instance = await db.AlertInstances.IgnoreQueryFilters()
            .FirstAsync(i => i.Id == instanceId);
        instance.Status.Should().Be("acknowledged");

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync("alert_acknowledged", It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_AlreadyAcked_NoOp()
    {
        var (excursionId, instanceId) = await SeedActiveExcursionAsync(
            acknowledgedAt: DateTime.UtcNow.AddMinutes(-1));

        await using (var db = NewUnfilteredContext())
        {
            var instance = await db.AlertInstances.IgnoreQueryFilters()
                .FirstAsync(i => i.Id == instanceId);
            instance.Status = "acknowledged";
            await db.SaveChangesAsync();
        }

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "user:bob", AlertAcknowledgementAuthority.System, broadcast: true, CancellationToken.None);

        await using var db2 = NewUnfilteredContext();
        var excursion = await db2.AlertExcursions.IgnoreQueryFilters()
            .FirstAsync(e => e.Id == excursionId);
        excursion.AcknowledgedBy.Should().NotBe("user:bob");

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task AcknowledgeExcursion_ClosedExcursion_NoOp()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync(endedAt: DateTime.UtcNow.AddMinutes(-1));

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "user:bob", AlertAcknowledgementAuthority.System, broadcast: true, CancellationToken.None);

        await using var db = NewUnfilteredContext();
        var excursion = await db.AlertExcursions.IgnoreQueryFilters()
            .FirstAsync(e => e.Id == excursionId);
        excursion.AcknowledgedAt.Should().BeNull();

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task AcknowledgeExcursion_FutureEndedTestFire_StampsAck()
    {
        // A device_action test fire carries a short future EndedAt so it surfaces in the
        // active-intents snapshot (AlertDeliveryService.TestFireAsync). While that window is
        // open the tray is flashing — acknowledging it must work, not silently no-op.
        var (excursionId, instanceId) = await SeedActiveExcursionAsync(
            endedAt: DateTime.UtcNow.AddSeconds(90));

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "user:bob", AlertAcknowledgementAuthority.System, broadcast: true, CancellationToken.None);

        await using var db = NewUnfilteredContext();
        var excursion = await db.AlertExcursions.IgnoreQueryFilters()
            .FirstAsync(e => e.Id == excursionId);
        excursion.AcknowledgedAt.Should().NotBeNull();
        excursion.AcknowledgedBy.Should().Be("user:bob");

        var instance = await db.AlertInstances.IgnoreQueryFilters()
            .FirstAsync(i => i.Id == instanceId);
        instance.Status.Should().Be("acknowledged");

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync("alert_acknowledged", It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_NotFound_NoOp()
    {
        await _service.AcknowledgeExcursionAsync(_tenantId, Guid.NewGuid(), "user:bob", AlertAcknowledgementAuthority.System, broadcast: true, CancellationToken.None);

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    // ---- AcknowledgeExcursionAsync: acknowledge for everyone or mute for the caller ----

    private static readonly string[] ViewerPermissions =
        [Scope.GlucoseRead, Scope.ReportsRead, Scope.DeviceNotify, Scope.DeviceActuate];

    private AlertAcknowledgementAuthority Member(
        Guid subjectId, IEnumerable<string> membership, params string[] credentialScopes)
    {
        _members
            .Setup(m => m.GetEffectivePermissionsAsync(subjectId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership.ToHashSet());
        return new AlertAcknowledgementAuthority(subjectId, credentialScopes.ToHashSet());
    }

    private async Task<List<AlertExcursionMuteEntity>> MutesAsync()
    {
        await using var db = NewUnfilteredContext();
        return await db.AlertExcursionMutes.IgnoreQueryFilters().ToListAsync();
    }

    [Fact]
    public async Task AcknowledgeExcursion_ViewerWithDeviceNotifyOnly_MutesForThemselvesAndLeavesEscalationRunning()
    {
        var (excursionId, instanceId) = await SeedActiveExcursionAsync();
        var viewer = Guid.NewGuid();

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:viewer", Member(viewer, ViewerPermissions, Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
        await using var db = NewUnfilteredContext();
        var excursion = await db.AlertExcursions.IgnoreQueryFilters().FirstAsync(e => e.Id == excursionId);
        excursion.AcknowledgedAt.Should().BeNull("a mute must not acknowledge the excursion for everyone");
        var instance = await db.AlertInstances.IgnoreQueryFilters().FirstAsync(i => i.Id == instanceId);
        instance.Status.Should().Be("triggered");

        var mute = (await MutesAsync()).Should().ContainSingle().Subject;
        mute.SubjectId.Should().Be(viewer);
        mute.AlertExcursionId.Should().Be(excursionId);
        mute.TenantId.Should().Be(_tenantId);

        _broadcast.Verify(x => x.BroadcastAlertEventAsync("alert_acknowledged", It.IsAny<object>()), Times.Never);
        _broadcast.Verify(
            x => x.BroadcastDeviceActionToSubjectAsync(
                viewer,
                It.Is<DeviceActionIntent>(i =>
                    i.ExcursionId == excursionId && i.Acknowledged && i.Intent == "acknowledged")),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_RepeatMute_KeepsOneRow()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync();
        var authority = Member(Guid.NewGuid(), ViewerPermissions, Scope.DeviceNotify);

        await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:viewer", authority, broadcast: true, CancellationToken.None);
        await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:viewer", authority, broadcast: true, CancellationToken.None);

        (await MutesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task AcknowledgeExcursion_MuteThatLosesTheInsertRace_StillReportsMutedAndNudgesTheDevices()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync();
        var viewer = Guid.NewGuid();
        var racingOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .AddInterceptors(new ConcurrentMuteWinsInterceptor(_options))
            .Options;
        var service = new AlertAcknowledgementService(
            new TestDbContextFactory(racingOptions) { TenantOverride = _tenantId },
            _tenantAccessor,
            _broadcast.Object,
            _members.Object,
            NullLogger<AlertAcknowledgementService>.Instance);

        var outcome = await service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:viewer", Member(viewer, ViewerPermissions, Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
        (await MutesAsync()).Should().ContainSingle().Which.SubjectId.Should().Be(viewer);
        _broadcast.Verify(
            x => x.BroadcastDeviceActionToSubjectAsync(viewer, It.IsAny<DeviceActionIntent>()), Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_CredentialWithAlertsReadWrite_AcknowledgesForEveryone()
    {
        var (excursionId, instanceId) = await SeedActiveExcursionAsync();
        var authority = new AlertAcknowledgementAuthority(
            Guid.NewGuid(), new HashSet<string> { Scope.AlertsReadWrite, Scope.DeviceNotify });

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:caretaker", authority, broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
        await using var db = NewUnfilteredContext();
        (await db.AlertExcursions.IgnoreQueryFilters().FirstAsync(e => e.Id == excursionId))
            .AcknowledgedBy.Should().Be("user:caretaker");
        (await db.AlertInstances.IgnoreQueryFilters().FirstAsync(i => i.Id == instanceId))
            .Status.Should().Be("acknowledged");
        (await MutesAsync()).Should().BeEmpty();
        _broadcast.Verify(x => x.BroadcastAlertEventAsync("alert_acknowledged", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task AcknowledgeExcursion_OwnersDeviceScopedCredential_AcknowledgesForEveryone()
    {
        // The Companion's grant resolves to device.notify alone even for an owner, so the decision
        // has to look past the token to the membership.
        var (excursionId, _) = await SeedActiveExcursionAsync();

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:owner", Member(Guid.NewGuid(), [Scope.FullAccess], Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
        await using var db = NewUnfilteredContext();
        (await db.AlertExcursions.IgnoreQueryFilters().FirstAsync(e => e.Id == excursionId))
            .AcknowledgedAt.Should().NotBeNull();
        (await MutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AcknowledgeExcursion_DeviceCredentialOfMemberHoldingAlertsReadWrite_AcknowledgesForEveryone()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync();

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:caretaker",
            Member(Guid.NewGuid(), [Scope.GlucoseRead, Scope.AlertsReadWrite], Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
    }

    [Fact]
    public async Task AcknowledgeExcursion_ClinicianWithAlertsRead_Mutes()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync();

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:clinician",
            Member(Guid.NewGuid(), [Scope.GlucoseRead, Scope.AlertsRead, Scope.DeviceNotify], Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
    }

    [Fact]
    public async Task AcknowledgeExcursion_SubjectWithNoMembership_Mutes()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync();
        var subject = Guid.NewGuid();
        _members
            .Setup(m => m.GetEffectivePermissionsAsync(subject, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>?)null);

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:someone",
            new AlertAcknowledgementAuthority(subject, new HashSet<string> { Scope.DeviceNotify }),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Muted);
    }

    [Fact]
    public async Task AcknowledgeExcursion_ViewerOnAlreadyAcknowledgedExcursion_ReportsAcknowledgedWithoutMuting()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync(acknowledgedAt: DateTime.UtcNow.AddMinutes(-1));

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:viewer", Member(Guid.NewGuid(), ViewerPermissions, Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Acknowledged);
        (await MutesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AcknowledgeExcursion_ViewerOnClosedExcursion_ReportsClosedWithoutMuting()
    {
        var (excursionId, _) = await SeedActiveExcursionAsync(endedAt: DateTime.UtcNow.AddMinutes(-1));

        var outcome = await _service.AcknowledgeExcursionAsync(
            _tenantId, excursionId, "user:viewer", Member(Guid.NewGuid(), ViewerPermissions, Scope.DeviceNotify),
            broadcast: true, CancellationToken.None);

        outcome.Should().Be(AlertAcknowledgementOutcome.Closed);
        (await MutesAsync()).Should().BeEmpty();
    }

    // ---- AcknowledgeAllAsync ----

    [Fact]
    public async Task AcknowledgeAll_MultipleOpenExcursions_AcksAllAndBroadcastsOnce()
    {
        var (e1, i1) = await SeedActiveExcursionAsync();
        var (e2, i2) = await SeedActiveExcursionAsync();
        // Different tenant should be untouched
        var (eOther, iOther) = await SeedActiveExcursionAsync(tenantId: Guid.NewGuid());

        await _service.AcknowledgeAllAsync(_tenantId, "user:bob", CancellationToken.None);

        await using var db = NewUnfilteredContext();
        var allExc = await db.AlertExcursions.IgnoreQueryFilters().ToListAsync();
        allExc.First(e => e.Id == e1).AcknowledgedBy.Should().Be("user:bob");
        allExc.First(e => e.Id == e2).AcknowledgedBy.Should().Be("user:bob");
        allExc.First(e => e.Id == eOther).AcknowledgedBy.Should().BeNull();

        var allInst = await db.AlertInstances.IgnoreQueryFilters().ToListAsync();
        allInst.First(i => i.Id == i1).Status.Should().Be("acknowledged");
        allInst.First(i => i.Id == i2).Status.Should().Be("acknowledged");
        allInst.First(i => i.Id == iOther).Status.Should().Be("triggered");

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync("alert_acknowledged", It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task AcknowledgeAll_IncludesFutureEndedTestFire_ExcludesPastEnded()
    {
        var (openId, _) = await SeedActiveExcursionAsync();
        var (testFireId, _) = await SeedActiveExcursionAsync(endedAt: DateTime.UtcNow.AddSeconds(90));
        var (closedId, _) = await SeedActiveExcursionAsync(endedAt: DateTime.UtcNow.AddMinutes(-1));

        await _service.AcknowledgeAllAsync(_tenantId, "user:bob", CancellationToken.None);

        await using var db = NewUnfilteredContext();
        var allExc = await db.AlertExcursions.IgnoreQueryFilters().ToListAsync();
        allExc.First(e => e.Id == openId).AcknowledgedBy.Should().Be("user:bob");
        allExc.First(e => e.Id == testFireId).AcknowledgedBy.Should().Be("user:bob");
        allExc.First(e => e.Id == closedId).AcknowledgedBy.Should().BeNull();
    }

    [Fact]
    public async Task AcknowledgeAll_NoActiveExcursions_NoOp()
    {
        await _service.AcknowledgeAllAsync(_tenantId, "user:bob", CancellationToken.None);

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task AcknowledgeAllAsync_CarriesTheScopeAuditContextOntoEachContext()
    {
        // Acknowledgement mutates an auditable entity, and the pooled context is leased with
        // AuditContext cleared. Left unset, an auto-acknowledgement made inside a connector
        // sync's scope is attributed to whichever HTTP request the interceptor falls back to.
        await SeedActiveExcursionAsync();
        var audit = SystemAuditContext.ForService("connector:test");
        var service = new AlertAcknowledgementService(
            _factory,
            _tenantAccessor,
            _broadcast.Object,
            _members.Object,
            NullLogger<AlertAcknowledgementService>.Instance,
            audit);

        await service.AcknowledgeAllAsync(_tenantId, "system", CancellationToken.None);

        _factory.Created.Should().NotBeEmpty();
        _factory.Created.Should().OnlyContain(c => c.AuditContext == audit);
    }

    /// <summary>
    /// Commits the same mute from another context just before the service's own insert, then
    /// rejects that insert the way PostgreSQL's unique index would. The in-memory provider does
    /// not enforce unique indexes, so the rejection is raised here.
    /// </summary>
    private sealed class ConcurrentMuteWinsInterceptor(DbContextOptions<NocturneDbContext> winnerOptions)
        : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var losing = eventData.Context!.ChangeTracker.Entries<AlertExcursionMuteEntity>()
                .SingleOrDefault(e => e.State == EntityState.Added)?.Entity;
            if (losing is null)
                return result;

            await using var winner = new NocturneDbContext(winnerOptions) { TenantId = losing.TenantId };
            winner.AlertExcursionMutes.Add(new AlertExcursionMuteEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = losing.TenantId,
                SubjectId = losing.SubjectId,
                AlertExcursionId = losing.AlertExcursionId,
                CreatedAt = losing.CreatedAt,
            });
            await winner.SaveChangesAsync(ct);

            throw new DbUpdateException(
                "duplicate key value violates unique constraint",
                new PostgresException(
                    "duplicate key value violates unique constraint", "ERROR", "ERROR",
                    PostgresErrorCodes.UniqueViolation));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        // Optional override so AcknowledgeExcursionAsync (which doesn't take a tenant) can still
        // resolve the excursion across tenants under the global query filter.
        public Guid? TenantOverride { get; set; }

        public List<NocturneDbContext> Created { get; } = [];

        public NocturneDbContext CreateDbContext()
        {
            var ctx = new NocturneDbContext(options);
            if (TenantOverride is { } t)
            {
                ctx.TenantId = t;
            }
            Created.Add(ctx);
            return ctx;
        }

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
