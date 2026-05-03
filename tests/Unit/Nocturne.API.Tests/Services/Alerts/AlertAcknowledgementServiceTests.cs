using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertAcknowledgementServiceTests
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly AlertAcknowledgementService _service;

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertAcknowledgementServiceTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"acknowledgement_tests_{Guid.NewGuid()}")
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }
        _factory = new TestDbContextFactory(_options) { TenantOverride = _tenantId };

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        _service = new AlertAcknowledgementService(
            _factory,
            tenantAccessor.Object,
            _broadcast.Object,
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

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "system:auto-ack-on-trigger", broadcast: true, CancellationToken.None);

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

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "user:bob", broadcast: true, CancellationToken.None);

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
        var (excursionId, _) = await SeedActiveExcursionAsync(endedAt: DateTime.UtcNow);

        await _service.AcknowledgeExcursionAsync(_tenantId, excursionId, "user:bob", broadcast: true, CancellationToken.None);

        await using var db = NewUnfilteredContext();
        var excursion = await db.AlertExcursions.IgnoreQueryFilters()
            .FirstAsync(e => e.Id == excursionId);
        excursion.AcknowledgedAt.Should().BeNull();

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task AcknowledgeExcursion_NotFound_NoOp()
    {
        await _service.AcknowledgeExcursionAsync(_tenantId, Guid.NewGuid(), "user:bob", broadcast: true, CancellationToken.None);

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
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
    public async Task AcknowledgeAll_NoActiveExcursions_NoOp()
    {
        await _service.AcknowledgeAllAsync(_tenantId, "user:bob", CancellationToken.None);

        _broadcast.Verify(
            x => x.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        // Optional override so AcknowledgeExcursionAsync (which doesn't take a tenant) can still
        // resolve the excursion across tenants under the global query filter.
        public Guid? TenantOverride { get; set; }

        public NocturneDbContext CreateDbContext()
        {
            var ctx = new NocturneDbContext(options);
            if (TenantOverride is { } t)
            {
                ctx.TenantId = t;
            }
            return ctx;
        }

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
