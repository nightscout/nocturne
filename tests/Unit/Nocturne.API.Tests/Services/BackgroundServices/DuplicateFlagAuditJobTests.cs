using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class DuplicateFlagAuditJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDeduplicationService> _dedup = new();
    private readonly ListLogger<DuplicateFlagAuditJob> _logger = new();

    [Fact]
    public async Task Drift_is_logged_as_a_warning_per_record_type_with_its_counts()
    {
        Audits(
            new DuplicateFlagDrift(RecordType.SensorGlucose, 3, 5, 8),
            new DuplicateFlagDrift(RecordType.StateSpan, 0, 1, 0));

        var outcome = await RunAsync(active: true);

        _logger.Warnings.Should().HaveCount(2);
        _logger.Warnings.Should().ContainSingle(m => m.Contains("SensorGlucose")
            && m.Contains("3 flagged with no non-primary link") && m.Contains("5 with a non-primary link not flagged")
            && m.Contains("8 repaired"));
        _logger.Warnings.Should().ContainSingle(m => m.Contains("StateSpan") && m.Contains("0 repaired"));
        outcome.Should().Be(JobOutcome.Resume(null, Now + DuplicateFlagAuditJob.Interval));
    }

    [Fact]
    public async Task No_drift_logs_nothing_above_debug_and_comes_due_a_day_later()
    {
        Audits();

        var outcome = await RunAsync(active: true);

        _logger.Entries.Should().OnlyContain(e => e.Level <= LogLevel.Debug);
        outcome.Should().Be(JobOutcome.Resume(null, Now + TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task An_inactive_tenant_is_parked_without_an_audit()
    {
        var outcome = await RunAsync(active: false);

        _dedup.VerifyNoOtherCalls();
        outcome.Should().Be(JobOutcome.Resume(null, Now + IScheduledJob.InactiveTenantRecheck));
    }

    [Fact]
    public void The_audit_is_enrolled_per_tenant_and_spread_over_its_day()
    {
        var job = new DuplicateFlagAuditJob(_logger);

        job.Enrolment.Should().Be(JobEnrolment.PerTenant);
        job.Spread.Should().Be(TimeSpan.FromDays(1));
    }

    private void Audits(params DuplicateFlagDrift[] drift) =>
        _dedup.Setup(d => d.AuditDuplicateFlagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(drift);

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection().AddSingleton(_dedup.Object).BuildServiceProvider();
        var tenant = new TenantContext(Guid.CreateVersion7(), "t", "T", active, IsDemo: false);
        return await new DuplicateFlagAuditJob(_logger)
            .RunAsync(new JobRun(tenant, null, 0, services, Now), CancellationToken.None);
    }
}
