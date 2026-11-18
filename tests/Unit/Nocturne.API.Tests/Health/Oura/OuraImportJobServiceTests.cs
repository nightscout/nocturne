using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services;
using Nocturne.API.Services.Health.Oura;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Health.Oura;

[Trait("Category", "Unit")]
public class OuraImportJobServiceTests
{
    private readonly OuraImportHarness _h = new();

    [Fact]
    public async Task Start_ReturnsWhileTheImportRuns_AndTheStatusReportsProgressThenTheFinalCounts()
    {
        _h.StoreSession = _ => _h.SleepGate.Task;
        var (jobs, root) = _h.Build();

        var started = jobs.Start(OuraImportHarness.Export(), OuraImportHarness.Caller(root));

        started.State.Should().Be(OuraImportState.Running);
        var running = jobs.GetLatest(OuraImportHarness.Tenant.TenantId)!;
        running.State.Should().Be(OuraImportState.Running);
        running.Phase.Should().Be(OuraImportPhase.Sleep);
        running.SleepSessions.Should().Be(0);

        _h.SleepGate.SetResult();
        var ended = await OuraImportHarness.EndOf(jobs);

        ended.State.Should().Be(OuraImportState.Completed);
        ended.Phase.Should().BeNull();
        (ended.SleepSessions, ended.HeartRates, ended.StepDays).Should().Be((2, 2, 1));
        ended.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task TheImport_WritesUnderTheCallersTenant()
    {
        var (jobs, root) = _h.Build();

        jobs.Start(OuraImportHarness.Export(), OuraImportHarness.Caller(root));
        await OuraImportHarness.EndOf(jobs);

        _h.WrittenUnder.Should().HaveCount(2).And.OnlyContain(id => id == OuraImportHarness.Tenant.TenantId);
    }

    [Fact]
    public async Task AnImportThatFailsAfterWritingRecords_IsPartial_WithWhatItWrote()
    {
        _h.StoreHeartRates = _ => throw new InvalidOperationException("the database went away");
        var (jobs, root) = _h.Build();

        jobs.Start(OuraImportHarness.Export(), OuraImportHarness.Caller(root));
        var ended = await OuraImportHarness.EndOf(jobs);

        ended.State.Should().Be(OuraImportState.Partial);
        ended.SleepSessions.Should().Be(2);
        ended.HeartRates.Should().Be(0);
        ended.ErrorCode.Should().Be(OuraImportJobService.ImportFailedCode);
    }

    [Fact]
    public async Task AnImportThatFailsBeforeWritingAnything_HasFailed()
    {
        _h.StoreSession = _ => throw new InvalidOperationException("the database went away");
        var (jobs, root) = _h.Build();

        jobs.Start(OuraImportHarness.Export(), OuraImportHarness.Caller(root));
        var ended = await OuraImportHarness.EndOf(jobs);

        ended.State.Should().Be(OuraImportState.Failed);
        ended.ErrorCode.Should().Be(OuraImportJobService.ImportFailedCode);
    }

    [Fact]
    public async Task ASecondStartWhileOneRuns_IsRefused_AndADeleteWaitsForIt()
    {
        _h.StoreSession = _ => _h.SleepGate.Task;
        var (jobs, root) = _h.Build();
        var first = jobs.Start(OuraImportHarness.Export(), OuraImportHarness.Caller(root));
        var accessor = new OuraImportHarness.Accessor();
        accessor.SetTenant(OuraImportHarness.Tenant);
        var purgeGuard = new OuraImportPurgeGuard(_h.RunGuard, accessor);

        var second = () => jobs.Start(OuraImportHarness.Export(), OuraImportHarness.Caller(root));

        second.Should().Throw<OuraImportBlockedException>().Which.ByDelete.Should().BeFalse();
        jobs.Invoking(j => j.EnsureCanStart(OuraImportHarness.Tenant.TenantId)).Should().Throw<OuraImportBlockedException>();
        (await purgeGuard.PauseSyncsAsync(CancellationToken.None)).Should().BeNull();
        jobs.GetLatest(OuraImportHarness.Tenant.TenantId)!.JobId.Should().Be(first.JobId);

        _h.SleepGate.SetResult();
        await OuraImportHarness.EndOf(jobs);
        await using var paused = await purgeGuard.PauseSyncsAsync(CancellationToken.None);
        paused.Should().NotBeNull();
        second.Should().Throw<OuraImportBlockedException>("a delete of the tenant's Oura data is running")
            .Which.ByDelete.Should().BeTrue();
    }
}
