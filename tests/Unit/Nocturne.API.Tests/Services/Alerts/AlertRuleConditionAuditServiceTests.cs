using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertRuleConditionAuditServiceTests
{
    private const string Rejected = """{"operator":"and","conditions":[]}""";
    private const string Accepted = """{"direction":"below","value":70}""";

    private static readonly Guid ActiveTenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid InactiveTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FlaggedRule = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly DbContextOptions<NocturneDbContext> _options = new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    private readonly Mock<IAlertRuleConditionValidator> _validator = new();
    private readonly ListLogger<AlertRuleConditionAuditService> _logger = new();

    public AlertRuleConditionAuditServiceTests()
    {
        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((AlertConditionType _, string body, bool _, string? _, string? _) => body == Rejected
                ? [new RustValidationIssue("condition", "composite", "conditions_empty", "conditions")]
                : []);
    }

    private static AlertRuleEntity Rule(Guid id, Guid tenantId, string body, bool enabled = true) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = "rule",
        ConditionType = AlertConditionType.Composite,
        ConditionParams = body,
        IsEnabled = enabled,
    };

    private async Task SeedAsync()
    {
        await using var db = new NocturneDbContext(_options);
        db.Tenants.Add(new TenantEntity { Id = ActiveTenant, Slug = "active", IsActive = true });
        db.Tenants.Add(new TenantEntity { Id = InactiveTenant, Slug = "inactive", IsActive = false });
        db.AlertRules.AddRange(
            Rule(FlaggedRule, ActiveTenant, Rejected),
            Rule(Guid.NewGuid(), ActiveTenant, Rejected, enabled: false),
            Rule(Guid.NewGuid(), ActiveTenant, Accepted),
            Rule(Guid.NewGuid(), InactiveTenant, Rejected));
        await db.SaveChangesAsync();
    }

    private async Task RunAsync()
    {
        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_options));
        var services = new ServiceCollection()
            .AddSingleton(factory.Object)
            .AddSingleton(_validator.Object)
            .BuildServiceProvider();

        var service = new AlertRuleConditionAuditService(services, _logger);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
    }

    [Fact]
    public async Task Logs_each_enabled_rule_of_an_active_tenant_a_save_would_reject()
    {
        await SeedAsync();

        await RunAsync();

        _logger.Warnings.Should().HaveCount(2);
        _logger.Warnings.Should().ContainSingle(w =>
            w.Contains(FlaggedRule.ToString()) && w.Contains("condition composite: conditions_empty"));
        _logger.Warnings.Should().ContainSingle(w => w.StartsWith("1 enabled alert rule(s)"));
    }

    [Fact]
    public async Task Logs_nothing_when_every_rule_is_accepted()
    {
        await using (var db = new NocturneDbContext(_options))
        {
            db.Tenants.Add(new TenantEntity { Id = ActiveTenant, Slug = "active", IsActive = true });
            db.AlertRules.Add(Rule(Guid.NewGuid(), ActiveTenant, Accepted));
            await db.SaveChangesAsync();
        }

        await RunAsync();

        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_tenant_does_not_stop_the_others()
    {
        var secondTenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var secondRule = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
        await SeedAsync();
        await using (var db = new NocturneDbContext(_options))
        {
            db.Tenants.Add(new TenantEntity { Id = secondTenant, Slug = "second", IsActive = true });
            db.AlertRules.Add(Rule(secondRule, secondTenant, Rejected));
            await db.SaveChangesAsync();
        }
        var calls = 0;
        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), Rejected, It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(() => ++calls == 1
                ? throw new InvalidOperationException("boom")
                : [new RustValidationIssue("condition", "composite", "conditions_empty", "conditions")]);

        await RunAsync();

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
        _logger.Warnings.Should().ContainSingle(w => w.StartsWith("1 enabled alert rule(s)"));
    }

    [Fact]
    public async Task A_failure_is_logged_rather_than_thrown()
    {
        await SeedAsync();
        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("boom"));

        await RunAsync();

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
