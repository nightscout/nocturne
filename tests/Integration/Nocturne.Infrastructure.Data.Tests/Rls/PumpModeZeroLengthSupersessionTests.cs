using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A suspension dated a day ahead arrives while a real one is open, and the store ends the real one at
/// its start. Once the ahead span is ended at its own start it ends nothing, so the store releases the
/// real span, which stays open until its own resume.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class PumpModeZeroLengthSupersessionTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ahead = Now.AddDays(1);

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public PumpModeZeroLengthSupersessionTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task EndingTheAheadSpanAtItsOwnStart_ReopensTheRealSpan_WhichALaterResumeCloses()
    {
        await UpsertAsync(Suspension("real", Now, null));
        await UpsertAsync(Suspension("ahead", Ahead, null));
        (await ReadAsync("real")).EndTimestamp.Should().Be(Ahead, "the store ends an open span at the next one of its state");

        await UpdateAsync(Suspension("ahead", Ahead, Ahead));

        var reopened = await ReadAsync("real");
        reopened.EndTimestamp.Should().BeNull();
        reopened.SupersededById.Should().BeNull();

        await UpdateAsync(Suspension("real", Now, Now.AddHours(1)));

        (await ReadAsync("real")).EndTimestamp.Should().Be(Now.AddHours(1));
        (await ReadAsync("ahead")).EndTimestamp.Should().Be(Ahead);
    }

    /// <summary>The fold that ends the ahead span on a resume ends the real one in the same write.</summary>
    [Fact]
    public async Task OneWriteEndingTheAheadSpanAndTheRealOne_LeavesTheRealOneEndedAtTheResume()
    {
        await UpsertAsync(Suspension("real", Now, null));
        await UpsertAsync(Suspension("ahead", Ahead, null));

        await UpdateAsync(Suspension("ahead", Ahead, Ahead), Suspension("real", Now, Now.AddHours(1)));

        var real = await ReadAsync("real");
        real.EndTimestamp.Should().Be(Now.AddHours(1));
        real.SupersededById.Should().BeNull();
        (await ReadAsync("ahead")).EndTimestamp.Should().Be(Ahead);
    }

    /// <summary>The order the issue names: the ahead span is stored first, then a real suspension and its resume.</summary>
    [Fact]
    public async Task AnAheadSpanEndedAtItsOwnStart_DoesNotEndARealSuspensionStoredAfterIt()
    {
        await UpsertAsync(Suspension("ahead", Ahead, null));
        await UpdateAsync(Suspension("ahead", Ahead, Ahead));

        await UpsertAsync(Suspension("real", Now, null));
        (await ReadAsync("real")).EndTimestamp.Should().BeNull();

        await UpdateAsync(Suspension("real", Now, Now.AddHours(1)));
        (await ReadAsync("real")).EndTimestamp.Should().Be(Now.AddHours(1));
    }

    private static StateSpan Suspension(string originalId, DateTime start, DateTime? end) => new()
    {
        Id = originalId,
        OriginalId = originalId,
        Source = "pump",
        Category = StateSpanCategory.PumpMode,
        State = PumpModeState.Suspended.ToString(),
        StartTimestamp = start,
        EndTimestamp = end,
    };

    private async Task UpsertAsync(StateSpan span)
    {
        await using var scope = _provider.CreateAsyncScope();
        await Repository(scope).BulkUpsertAsync([span]);
    }

    private async Task UpdateAsync(params StateSpan[] spans)
    {
        await using var scope = _provider.CreateAsyncScope();
        (await Repository(scope).UpdateStateSpansAsync(spans)).Should().NotContainNulls();
    }

    private StateSpanRepository Repository(AsyncServiceScope scope)
    {
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        return new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance);
    }

    private async Task<StateSpanEntity> ReadAsync(string originalId)
    {
        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        return await read.StateSpans.AsNoTracking().SingleAsync(s => s.LegacyId == originalId);
    }
}
