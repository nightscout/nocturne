using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertSweepServiceAuditAttributionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    /// <summary>
    /// The orchestrator auto-acknowledges Info-severity excursions, and
    /// <see cref="AlertAcknowledgementService"/> carries the scope's audit context onto the
    /// contexts it creates. The sweep has no request and no actor, so both the ambient context
    /// and the scope's own DbContext must be system-attributed or those acknowledgements land as
    /// user mutations with every actor field null.
    /// </summary>
    [Fact]
    public async Task EvaluateTrackerAgeRulesAsync_SystemAttributesTheTenantScope()
    {
        var rule = new AlertRuleSnapshot(
            Guid.NewGuid(), Tenant, "sensor expired", AlertConditionType.TrackerAge,
            "{}", AlertRuleSeverity.Info, "{}", 0, false, null);

        var repository = new Mock<IAlertRepository>();
        repository
            .Setup(x => x.GetEnabledRulesByConditionTypeAsync(AlertConditionType.TrackerAge, It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        repository
            .Setup(x => x.GetTenantAlertContextAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAlertContext(Tenant, "owner", "slug", "Slug", true, null));

        IAuditContext? scopeContextAudit = null;
        bool? ambientIsSystemMutation = null;

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        services.AddScoped<ITenantAccessor>(_ => Mock.Of<ITenantAccessor>());
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(_ => new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite("DataSource=:memory:")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options));
        services.AddScoped<IAlertOrchestrator>(sp =>
        {
            var orchestrator = new Mock<IAlertOrchestrator>();
            orchestrator
                .Setup(x => x.EvaluateRulesAsync(
                    It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(),
                    It.IsAny<SensorContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    scopeContextAudit = sp.GetRequiredService<NocturneDbContext>().AuditContext;
                    ambientIsSystemMutation = sp.GetRequiredService<IAuditContext>().IsSystemMutation();
                    return Task.CompletedTask;
                });
            return orchestrator.Object;
        });

        var sut = new AlertSweepService(
            services.BuildServiceProvider(),
            NullLogger<AlertSweepService>.Instance);

        // Act
        await sut.EvaluateTrackerAgeRulesAsync(CancellationToken.None);

        // Assert
        scopeContextAudit.Should().NotBeNull();
        scopeContextAudit!.IsSystem.Should().BeTrue();
        scopeContextAudit.Endpoint.Should().Be("service:alert-sweep");
        ambientIsSystemMutation.Should().BeTrue();
    }
}
