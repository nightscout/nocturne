using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The clear of <c>superseded_by_id</c> on a soft delete reaches spans the deleting member's 24 hour
/// clamp hides, as the foreign key's <c>SetNull</c> did.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StateSpanSupersessionClampTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public StateSpanSupersessionClampTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task ClampedMemberDelete_ClearsThePointerOfAnOlderSpanTheClampHides()
    {
        var now = DateTime.UtcNow;
        var supersessor = Guid.NewGuid();
        var older = Guid.NewGuid();
        await using (var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant))
        {
            seed.StateSpans.AddRange(
                new StateSpanEntity
                {
                    Id = older, Category = "Override", State = "older",
                    StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-3), SupersededById = supersessor,
                },
                new StateSpanEntity
                {
                    Id = supersessor, Category = "Override", State = "open", StartTimestamp = now.AddDays(-3),
                });
            await seed.SaveChangesAsync();
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
            var repository = new StateSpanRepository(
                scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
                new Mock<IDeduplicationService>().Object,
                new Mock<IAuditContext>().Object,
                NullLogger<StateSpanRepository>.Instance);

            (await repository.DeleteStateSpanAsync(supersessor.ToString())).Should().NotBeEmpty();
        }

        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        (await read.StateSpans.AsNoTracking().SingleAsync(s => s.Id == older)).SupersededById.Should().BeNull();
    }
}
