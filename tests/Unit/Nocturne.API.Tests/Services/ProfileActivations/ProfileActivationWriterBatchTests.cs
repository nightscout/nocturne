using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

/// <summary>
/// A batch of activations the store takes only in part settles its family as if the refused ones were
/// never sent.
/// </summary>
public class ProfileActivationWriterBatchTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly NocturneDbContext _context = TestDbContextFactory.CreateInMemoryContext();
    private readonly ProfileActivationRepository _repository;
    private readonly ProfileActivationWriter _writer;

    public ProfileActivationWriterBatchTests()
    {
        _context.TenantId = TestTenantCache.DefaultTenantId;
        _repository = ProfileActivationTestDeps.Repository(_context);
        _writer = ProfileActivationTestDeps.Writer(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ARefusedSwitchLeavesTheActivationBeforeItRunningUntilTheNextOneTheStoreTook()
    {
        var running = await _repository.CreateAsync(Requested("Base", T0), WriteOrigin.Live);
        var refused = Requested("Sick", T0.AddHours(1));
        var taken = Requested("Sport", T0.AddHours(2));

        await _writer.WriteBatchAsync([new(refused, null), new(taken, null)], StoreRefusing(refused), CancellationToken.None);

        (await ReadAsync(running.Id)).EndTimestamp.Should().Be(T0.AddHours(2), "the refused switch never started");
        (await ReadAsync(taken.Id)).EndTimestamp.Should().BeNull();
        (await _context.ProfileActivations.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AnEffectiveSwitchConfirmingARefusedRequestConfirmsNothing()
    {
        var running = await _repository.CreateAsync(Requested("Base", T0), WriteOrigin.Live);
        var refused = Requested("Sport", T0.AddHours(1));
        var effective = Requested("Sport", T0.AddHours(1).AddMinutes(1));
        effective.Phase = ProfileActivationPhase.Effective;

        await _writer.WriteBatchAsync([new(refused, null), new(effective, null)], StoreRefusing(refused), CancellationToken.None);

        (await ReadAsync(effective.Id)).RequestedActivationId.Should().BeNull("the request it would confirm was never stored");
        (await ReadAsync(running.Id)).EndTimestamp.Should().BeNull("only a request ends the request before it");
    }

    [Fact]
    public async Task AnEffectiveSwitchConfirmingARequestInTheSameBatchIsLinkedOnceBothAreStored()
    {
        var requested = Requested("Sport", T0.AddHours(1));
        var effective = Requested("Sport", T0.AddHours(1).AddMinutes(1));
        effective.Phase = ProfileActivationPhase.Effective;
        var linkWhenStored = (Guid?)null;

        await _writer.WriteBatchAsync([new(requested, null), new(effective, null)], async batch =>
        {
            linkWhenStored = batch.Single(a => a.Id == effective.Id).RequestedActivationId;
            return await StoreRefusing(null)(batch);
        }, CancellationToken.None);

        linkWhenStored.Should().BeNull("a row is never stored naming one the same write may refuse");
        (await ReadAsync(effective.Id)).RequestedActivationId.Should().Be(requested.Id);
    }

    [Fact]
    public async Task AStoredActivationWhoseReplacementIsRefusedIsStillEndedByASwitchTheStoreTook()
    {
        var stored = await _repository.CreateAsync(Requested("Base", T0), WriteOrigin.Live);
        var replacement = Requested("Base", T0);
        replacement.Id = stored.Id;
        replacement.LegacyId = stored.LegacyId;
        replacement.Percentage = 120;
        var taken = Requested("Sport", T0.AddHours(2));

        await _writer.WriteBatchAsync([new(replacement, stored), new(taken, null)], StoreRefusing(replacement), CancellationToken.None);

        var row = await ReadAsync(stored.Id);
        row.Percentage.Should().Be(100, "its replacement was refused");
        row.EndTimestamp.Should().Be(T0.AddHours(2), "the switch the store took ends the row as read");
    }

    private Func<IReadOnlyList<ProfileActivation>, Task<IReadOnlyList<ProfileActivation?>>> StoreRefusing(ProfileActivation? refused) =>
        async batch =>
        {
            var taken = batch.Where(a => a.Id != refused?.Id).ToList();
            var written = (await _repository.BulkUpsertAsync(taken, WriteOrigin.Live)).ToDictionary(a => a.Id);
            return [.. batch.Select(a => written.GetValueOrDefault(a.Id))];
        };

    private async Task<ProfileActivation> ReadAsync(Guid id) =>
        (await _repository.GetByIdAsync(id))!;

    private static ProfileActivation Requested(string profile, DateTime start) => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = start,
        Phase = ProfileActivationPhase.Requested,
        BaseProfileName = profile,
        Percentage = 100,
        EnteredBy = "careportal",
        LegacyId = $"{profile}-{start:HHmm}",
    };
}
