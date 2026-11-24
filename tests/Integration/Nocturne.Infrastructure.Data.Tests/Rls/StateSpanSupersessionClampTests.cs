using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A soft delete undoes the supersession it gave spans the deleting member's 24 hour clamp hides, as the
/// foreign key's <c>SetNull</c> reached them, and ends them at successors the clamp hides. A restore ends
/// hidden spans at the restored span again.
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
    public async Task ClampedMemberDelete_ReopensAnOlderSpanTheClampHides()
    {
        var now = DateTime.UtcNow;
        var supersessor = Guid.NewGuid();
        var older = Guid.NewGuid();
        await SeedAsync(
            new StateSpanEntity
            {
                Id = older, Category = "TemporaryTarget", State = "older",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-3), SupersededById = supersessor,
            },
            new StateSpanEntity
            {
                Id = supersessor, Category = "TemporaryTarget", State = "open", StartTimestamp = now.AddDays(-3),
            });

        await DeleteAsClampedMemberAsync(supersessor);

        var reopened = await ReadAsync(older);
        reopened.EndTimestamp.Should().BeNull();
        reopened.SupersededById.Should().BeNull();
    }

    /// <summary>
    /// The successor is a snapshot of the deleted treatment's own override, which did not end it, and
    /// ended over a day ago, so the clamp hides it as well as the span it now ends.
    /// </summary>
    [Fact]
    public async Task ClampedMemberDelete_EndsAHiddenReopenedSpanAtASuccessorTheClampHides()
    {
        var now = DateTime.UtcNow;
        var supersessor = Guid.NewGuid();
        var older = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        await SeedAsync(
            new StateSpanEntity
            {
                Id = older, Category = "Override", State = "Custom",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-3), SupersededById = supersessor,
                MetadataJson = Metadata(StateSpanMetadataExtensions.TreatmentsCollection, "reason", "P Party"),
            },
            new StateSpanEntity
            {
                Id = supersessor, Category = "Override", State = "Custom", StartTimestamp = now.AddDays(-3),
                MetadataJson = Metadata(StateSpanMetadataExtensions.TreatmentsCollection, "reason", "N Night"),
            },
            new StateSpanEntity
            {
                Id = snapshot, Category = "Override", State = "Custom",
                StartTimestamp = now.AddDays(-2), EndTimestamp = now.AddDays(-1.5),
                MetadataJson = Metadata(StateSpanMetadataExtensions.DeviceStatusCollection, "name", "Night"),
            });

        await DeleteAsClampedMemberAsync(supersessor);

        var ended = await ReadAsync(older);
        ended.EndTimestamp.Should().BeCloseTo(now.AddDays(-2), TimeSpan.FromMilliseconds(1));
        ended.SupersededById.Should().Be(snapshot);
    }

    /// <summary>
    /// The older span ends at a span that ended over a day ago, so the clamp hides it before the restore
    /// and after it.
    /// </summary>
    [Fact]
    public async Task ClampedMemberRestore_EndsAnOlderSpanTheClampHidesAtTheRestoredSpan()
    {
        var now = DateTime.UtcNow;
        var older = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var later = Guid.NewGuid();
        await SeedAsync(
            new StateSpanEntity
            {
                Id = later, Category = "TemporaryTarget", State = "later",
                StartTimestamp = now.AddDays(-2), EndTimestamp = now.AddDays(-1.9),
            },
            new StateSpanEntity
            {
                Id = older, Category = "TemporaryTarget", State = "older",
                StartTimestamp = now.AddDays(-5), EndTimestamp = now.AddDays(-2), SupersededById = later,
            },
            new StateSpanEntity
            {
                Id = restored, Category = "TemporaryTarget", State = "restored",
                StartTimestamp = now.AddDays(-3), DeletedAt = now.AddHours(-1),
            });

        StateSpan answered;
        await using (var scope = _provider.CreateAsyncScope())
            answered = await ClampedRepository(scope).RestoreAsync(restored, WriteOrigin.Live);

        var ended = await ReadAsync(older);
        ended.EndTimestamp.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromMilliseconds(1));
        ended.SupersededById.Should().Be(restored);
        answered.SupersededById.Should().Be(later.ToString());
        (await ReadAsync(restored)).SupersededById.Should().Be(later);
    }

    private static string Metadata(string collection, string nameKey, string name) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [StateSpanMetadataExtensions.CollectionKey] = collection,
            [nameKey] = name,
        });

    private async Task SeedAsync(params StateSpanEntity[] spans)
    {
        await using var seed = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        seed.StateSpans.AddRange(spans);
        await seed.SaveChangesAsync();
    }

    private async Task DeleteAsClampedMemberAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        (await ClampedRepository(scope).DeleteStateSpanAsync(id.ToString())).Should().NotBeEmpty();
    }

    private StateSpanRepository ClampedRepository(AsyncServiceScope scope)
    {
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        return new StateSpanRepository(
            scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<StateSpanRepository>.Instance);
    }

    private async Task<StateSpanEntity> ReadAsync(Guid id)
    {
        await using var read = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        return await read.StateSpans.AsNoTracking().SingleAsync(s => s.Id == id);
    }
}
