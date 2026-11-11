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
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertSweepJobAuditAttributionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    /// <summary>
    /// The orchestrator auto-acknowledges Info-severity excursions on contexts that read the
    /// scope's audit context. The sweep has no request and no actor, so it must evaluate in the
    /// run's system-attributed scope, not one of its own, or those acknowledgements land as user
    /// mutations with every actor field null.
    /// </summary>
    [Fact]
    public async Task The_wall_clock_pass_evaluates_in_the_runs_system_attributed_scope()
    {
        var rule = new AlertRuleSnapshot(
            Guid.NewGuid(), Tenant, "sensor expired", AlertConditionType.TrackerAge,
            "{}", AlertRuleSeverity.Info, "{}", 0, false, null);

        var repository = AlertSweepRuns.Repository(Tenant, () => null);
        repository
            .Setup(x => x.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);

        IAuditContext? scopeContextAudit = null;
        bool? ambientIsSystemMutation = null;

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        services.AddSingleton(Mock.Of<ICanonicalGlucoseService>());
        services.AddScoped<ITenantAccessor>(_ => Mock.Of<ITenantAccessor>());
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(sp => new TenantReadContext(null, null, sp.GetRequiredService<IAuditContext>()));
        services.AddScoped<IAlertOrchestrator>(sp =>
        {
            var orchestrator = new Mock<IAlertOrchestrator>();
            orchestrator
                .Setup(x => x.EvaluateRulesAsync(
                    It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(),
                    It.IsAny<IReadOnlySet<Guid>>(),
                    It.IsAny<SensorContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    scopeContextAudit = sp.GetRequiredService<TenantReadContext>().AuditContext;
                    ambientIsSystemMutation = sp.GetRequiredService<IAuditContext>().IsSystemMutation();
                    return Task.CompletedTask;
                });
            return orchestrator.Object;
        });

        await new AlertSweepJob(NullLogger<AlertSweepJob>.Instance)
            .RunAsync(services.BuildServiceProvider(), Tenant, DateTimeOffset.UtcNow);

        scopeContextAudit.Should().NotBeNull();
        scopeContextAudit!.IsSystem.Should().BeTrue();
        scopeContextAudit.Endpoint.Should().Be("job:alert-sweep");
        ambientIsSystemMutation.Should().BeTrue();
    }
}
