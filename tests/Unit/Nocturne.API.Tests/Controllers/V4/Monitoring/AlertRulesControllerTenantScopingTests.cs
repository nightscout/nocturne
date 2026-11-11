using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Regression tests for tenant scoping on <see cref="AlertRulesController"/>.
///
/// Both the EF global query filter and PostgreSQL RLS derive from the context's <c>TenantId</c>;
/// a context reaching the wrong tenant made <c>/alerts</c> surface another tenant's rules, most
/// visibly the demo tenant's seeded "High"/"Low" rules. Every context
/// <see cref="Infrastructure.Data.Services.INocturneDbContextFactory"/> creates reads the
/// resolved tenant, which these tests lock in.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerTenantScopingTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task GetRules_ReturnsOnlyCurrentTenantRules_NotAnotherTenantsDemoDefaults()
    {
        // Arrange: a shared store where tenant B holds the demo "High"/"Low" rules and
        // tenant A holds its own custom rule.
        var options = NewStore();
        await SeedAsync(options,
            (TenantA, "Overnight Low"),
            (TenantB, "High"),
            (TenantB, "Low"));

        var controller = CreateControllerScopedTo(TenantA, options);

        // Act
        var result = await controller.GetRules(CancellationToken.None);

        // Assert: only tenant A's rule — never tenant B's seeded defaults.
        var rules = OkValue(result);
        rules.Should().ContainSingle().Which.Name.Should().Be("Overnight Low");
        rules.Select(r => r.Name).Should().NotContain(["High", "Low"]);
    }

    [Fact]
    public async Task GetRule_ById_DoesNotReturnAnotherTenantsRule()
    {
        // Arrange: a rule that belongs to tenant B.
        var options = NewStore();
        var tenantBRuleId = Guid.NewGuid();
        await SeedAsync(options, (TenantB, "High", tenantBRuleId));

        var controller = CreateControllerScopedTo(TenantA, options);

        // Act: tenant A asks for tenant B's rule by id.
        var result = await controller.GetRule(tenantBRuleId, CancellationToken.None);

        // Assert: it is invisible to tenant A, not leaked.
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    private static DbContextOptions<NocturneDbContext> NewStore() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task SeedAsync(
        DbContextOptions<NocturneDbContext> options,
        params (Guid TenantId, string Name, Guid Id)[] rules)
    {
        // Inserts are not subject to the tenant query filter, so a single context can seed
        // rows for multiple tenants.
        await using var seed = new NocturneDbContext(options);
        foreach (var (tenantId, name, id) in rules)
            seed.AlertRules.Add(NewRule(tenantId, name, id));
        await seed.SaveChangesAsync();
    }

    private static Task SeedAsync(
        DbContextOptions<NocturneDbContext> options,
        params (Guid TenantId, string Name)[] rules) =>
        SeedAsync(options, rules.Select(r => (r.TenantId, r.Name, Guid.NewGuid())).ToArray());

    private static AlertRulesController CreateControllerScopedTo(
        Guid tenantId, DbContextOptions<NocturneDbContext> options)
    {
        // Every context the factory creates reaches this tenant, as a production context reads
        // its scope's resolved tenant.
        var seedContext = new NocturneDbContext(options) { TenantId = tenantId };
        var factory = new TestNocturneDbContextFactory(seedContext);

        return new AlertRulesController(
            factory,
            new TenantMembershipRepository(factory),
            new AlertRepository(factory),
            new AlertRuleRepository(factory),
            MockTenantAccessor.Create(tenantId).Object,
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            new NoConditionIssues(),
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(new AlertRuleEvaluationGate()),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>()),
            Mock.Of<ILogger<AlertRulesController>>());
    }

    private static List<AlertRuleResponse> OkValue(ActionResult<List<AlertRuleResponse>> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<List<AlertRuleResponse>>().Subject;
    }

    private static AlertRuleEntity NewRule(Guid tenantId, string name, Guid id) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = name,
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = "{}",
        ClientConfiguration = "{}",
        Severity = AlertRuleSeverity.Warning,
        IsEnabled = true,
        SortOrder = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
