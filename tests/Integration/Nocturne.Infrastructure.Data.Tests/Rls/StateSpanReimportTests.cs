using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A re-upload of a state span's original id against the unique live index: a span the system swept
/// is written again beside its tombstone, and a span the user deleted stays deleted.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanReimportTests : IAsyncLifetime
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public StateSpanReimportTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_system_swept_id_is_written_again()
    {
        var swept = await SeedDeletedAsync("swept-1", deletedByUser: false);

        var written = await WithRepositoryAsync(r => r.BulkUpsertAsync([Span("swept-1")]));

        written.Should().ContainSingle().Which.Outcome.Should().Be(UpsertOutcome.Created);
        var rows = await RowsAsync("swept-1");
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.DeletedAt == null).Which.Id.Should().NotBe(swept);
    }

    [Fact]
    public async Task A_user_deleted_id_stays_deleted()
    {
        var tombstone = await SeedDeletedAsync("user-deleted-1", deletedByUser: true);

        var written = await WithRepositoryAsync(r => r.BulkUpsertAsync([Span("user-deleted-1")]));

        written.Should().ContainSingle().Which.Outcome.Should().BeNull();
        (await RowsAsync("user-deleted-1")).Should().ContainSingle()
            .Which.Should().Match<StateSpanEntity>(r => r.Id == tombstone && r.DeletedAt != null);
    }

    [Fact]
    public async Task A_live_id_is_updated_in_place()
    {
        await WithRepositoryAsync(r => r.BulkUpsertAsync([Span("live-1")]));

        var written = await WithRepositoryAsync(r => r.BulkUpsertAsync([Span("live-1", Origin.AddHours(2))]));

        written.Should().ContainSingle().Which.Outcome.Should().Be(UpsertOutcome.Updated);
        (await RowsAsync("live-1")).Should().ContainSingle().Which.EndTimestamp.Should().Be(Origin.AddHours(2));
    }

    private static StateSpan Span(string originalId, DateTime? end = null) => new()
    {
        Category = StateSpanCategory.Override,
        State = "Custom",
        StartTimestamp = Origin,
        EndTimestamp = end ?? Origin.AddHours(1),
        Source = "a",
        OriginalId = originalId,
    };

    private async Task<List<StateSpanEntity>> RowsAsync(string originalId)
    {
        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        return await read.StateSpans.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == _tenant && s.LegacyId == originalId)
            .ToListAsync();
    }

    private async Task<Guid> SeedDeletedAsync(string originalId, bool deletedByUser)
    {
        var span = new StateSpanEntity
        {
            Id = Guid.CreateVersion7(),
            Category = nameof(StateSpanCategory.Override),
            State = "Custom",
            Source = "a",
            LegacyId = originalId,
            StartTimestamp = Origin,
        };
        await using var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        seed.StateSpans.Add(span);
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE state_spans SET deleted_at = {Origin}, deleted_by_user = {deletedByUser} WHERE tenant_id = {_tenant} AND id = {span.Id}");
        return span.Id;
    }

    private async Task<T> WithRepositoryAsync<T>(Func<StateSpanRepository, Task<T>> write)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        return await write(new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance));
    }
}
