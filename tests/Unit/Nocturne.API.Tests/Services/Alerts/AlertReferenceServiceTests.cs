using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertReferenceServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly AlertReferenceService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertReferenceServiceTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"alert_reference_tests_{Guid.NewGuid()}")
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }
        _factory = new TestDbContextFactory(_options) { TenantOverride = _tenantId };

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        _sut = new AlertReferenceService(
            _factory,
            tenantAccessor.Object,
            NullLogger<AlertReferenceService>.Instance);
    }

    private async Task<Guid> SeedRuleAsync(AlertConditionType type, object conditionPayload)
    {
        await using var db = new NocturneDbContext(_options) { TenantId = _tenantId };
        var id = Guid.NewGuid();
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = id,
            TenantId = _tenantId,
            Name = "Test",
            ConditionType = type,
            ConditionParams = JsonSerializer.Serialize(conditionPayload, JsonOptions),
            ClientConfiguration = "{}",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---- FindReferencingRulesAsync ----

    [Fact]
    public async Task FindReferencing_DirectAlertStateReference_ReturnsReferencingId()
    {
        var target = await SeedRuleAsync(AlertConditionType.Threshold,
            new ThresholdCondition("below", 70m));
        var referencingId = await SeedRuleAsync(AlertConditionType.AlertState,
            new AlertStateCondition(target, "active", null));

        var refs = await _sut.FindReferencingRulesAsync(target, CancellationToken.None);

        refs.Should().BeEquivalentTo(new[] { referencingId });
    }

    [Fact]
    public async Task FindReferencing_NestedInComposite_ReturnsReferencingId()
    {
        var target = await SeedRuleAsync(AlertConditionType.Threshold,
            new ThresholdCondition("below", 70m));
        var nested = new CompositeCondition("and", new List<ConditionNode>
        {
            new("threshold", Threshold: new ThresholdCondition("above", 250m)),
            new("alert_state", AlertState: new AlertStateCondition(target, "active", null)),
        });
        var referencingId = await SeedRuleAsync(AlertConditionType.Composite, nested);

        var refs = await _sut.FindReferencingRulesAsync(target, CancellationToken.None);

        refs.Should().BeEquivalentTo(new[] { referencingId });
    }

    [Fact]
    public async Task FindReferencing_NoReferences_ReturnsEmpty()
    {
        var target = await SeedRuleAsync(AlertConditionType.Threshold,
            new ThresholdCondition("below", 70m));
        await SeedRuleAsync(AlertConditionType.Threshold, new ThresholdCondition("above", 200m));

        var refs = await _sut.FindReferencingRulesAsync(target, CancellationToken.None);

        refs.Should().BeEmpty();
    }

    // ---- DetectCycleAsync ----

    [Fact]
    public async Task DetectCycle_SelfReference_ReturnsTrue()
    {
        var ruleId = Guid.NewGuid();
        var node = new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(ruleId, "active", null));

        var cycle = await _sut.DetectCycleAsync(ruleId, node, CancellationToken.None);

        cycle.Should().BeTrue();
    }

    [Fact]
    public async Task DetectCycle_TwoStepCycle_ReturnsTrue()
    {
        // ruleA -> ruleB -> ruleA  (we're updating ruleA's tree to reference ruleB)
        var ruleA = Guid.NewGuid();
        var ruleB = await SeedRuleAsync(AlertConditionType.AlertState,
            new AlertStateCondition(ruleA, "active", null));

        var proposedRoot = new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(ruleB, "active", null));

        var cycle = await _sut.DetectCycleAsync(ruleA, proposedRoot, CancellationToken.None);

        cycle.Should().BeTrue();
    }

    [Fact]
    public async Task DetectCycle_AcyclicChain_ReturnsFalse()
    {
        // ruleA -> ruleB -> ruleC (terminates)
        var ruleA = Guid.NewGuid();
        var ruleC = await SeedRuleAsync(AlertConditionType.Threshold,
            new ThresholdCondition("below", 70m));
        var ruleB = await SeedRuleAsync(AlertConditionType.AlertState,
            new AlertStateCondition(ruleC, "active", null));

        var proposedRoot = new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(ruleB, "active", null));

        var cycle = await _sut.DetectCycleAsync(ruleA, proposedRoot, CancellationToken.None);

        cycle.Should().BeFalse();
    }

    [Fact]
    public async Task DetectCycle_NoRuleId_AlwaysFalse()
    {
        // Create path: ruleId is unknown -> can't form a cycle.
        var node = new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(Guid.NewGuid(), "active", null));

        var cycle = await _sut.DetectCycleAsync(ruleId: null, node, CancellationToken.None);

        cycle.Should().BeFalse();
    }

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public Guid? TenantOverride { get; set; }

        public NocturneDbContext CreateDbContext()
        {
            var ctx = new NocturneDbContext(options);
            if (TenantOverride is { } t) ctx.TenantId = t;
            return ctx;
        }

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
