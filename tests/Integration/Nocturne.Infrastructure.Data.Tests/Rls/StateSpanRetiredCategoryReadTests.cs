using Microsoft.EntityFrameworkCore;
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
/// The reads that include soft-deleted state spans pass over a row whose category was removed from
/// <see cref="StateSpanCategory"/> rather than fail on it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanRetiredCategoryReadTests : IAsyncLifetime
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public StateSpanRetiredCategoryReadTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveDeleted_PassesOverARetiredSpan(bool deletedByUser)
    {
        var retired = await SeedDeletedAsync("TempBasal", "retired-1", deletedByUser);
        var current = await SeedDeletedAsync("PumpMode", "current-1", deletedByUser);

        var resolved = await WithRepositoryAsync(r =>
            r.ResolveDeletedAsync(["retired-1", "current-1", retired.ToString()]));

        resolved.Keys.Should().BeEquivalentTo([current]);
        resolved[current].Category.Should().Be(StateSpanCategory.PumpMode);
    }

    [Fact]
    public async Task Upsert_IsNotBlockedByAUserDeletedRetiredSpanOfTheSameOriginalId()
    {
        await SeedDeletedAsync("BasalDelivery", "shared-1", deletedByUser: true);

        var written = await WithRepositoryAsync(r => r.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = nameof(PumpModeState.Automatic),
            StartTimestamp = Origin.AddDays(1),
            Source = "a",
            OriginalId = "shared-1",
        }));

        written.Category.Should().Be(StateSpanCategory.PumpMode);
        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        (await read.StateSpans.CountAsync(s => s.OriginalId == "shared-1")).Should().Be(1);
    }

    private async Task<Guid> SeedDeletedAsync(string category, string originalId, bool deletedByUser)
    {
        var span = new StateSpanEntity
        {
            Id = Guid.CreateVersion7(),
            Category = category,
            State = "Active",
            Source = "a",
            OriginalId = originalId,
            StartTimestamp = Origin,
        };
        await using var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        seed.StateSpans.Add(span);
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE state_spans SET deleted_at = {Origin}, deleted_by_user = {deletedByUser} WHERE tenant_id = {_tenant} AND id = {span.Id}");
        return span.Id;
    }

    private async Task<T> WithRepositoryAsync<T>(Func<StateSpanRepository, Task<T>> read)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        return await read(new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance,
            TimeProvider.System));
    }
}
