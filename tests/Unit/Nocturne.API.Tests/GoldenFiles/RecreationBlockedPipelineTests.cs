using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Tests.GoldenFiles;

/// <summary>
/// The whole request pipeline for a refused re-creation: only the globally-registered
/// <see cref="API.Filters.RecreationBlockedFilter"/> turns the repository's
/// <see cref="Core.Contracts.V4.Repositories.RecreationBlockedException"/> into a status a client
/// can act on, and nothing else in the suite exercises that registration.
/// </summary>
public class RecreationBlockedPipelineTests : GoldenFileTestBase
{
    private const string DataSource = "aaps";
    private const string SyncIdentifier = "pipeline-sync-1";

    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime T0 = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    public RecreationBlockedPipelineTests(GoldenFileWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task V4PostBolus_WhenAUserDeletedTombstoneHoldsTheSyncKey_Answers409()
    {
        var tombstoneId = await SeedUserDeletedBolusAsync();

        var response = await Client.PostAsJsonAsync("/api/v4/insulin/boluses", new
        {
            timestamp = T0,
            insulin = 9.0,
            dataSource = DataSource,
            syncIdentifier = SyncIdentifier,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain(SyncIdentifier);

        await using var verify = NewContext();
        var rows = await verify.Boluses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.SyncIdentifier == SyncIdentifier).ToListAsync();
        rows.Should().ContainSingle("the POST must not have inserted past the tombstone")
            .Which.Id.Should().Be(tombstoneId);
    }

    private NocturneDbContext NewContext()
    {
        var factory = Factory.Services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        var ctx = factory.CreateDbContext();
        ctx.TenantId = TestTenantId;
        return ctx;
    }

    private async Task<Guid> SeedUserDeletedBolusAsync()
    {
        await using var ctx = NewContext();
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = T0,
            DataSource = DataSource,
            SyncIdentifier = SyncIdentifier,
            Insulin = 5.0,
            DeletedAt = T0.AddHours(1),
        };
        var entry = ctx.Boluses.Add(entity);
        entry.Property("DeletedByUser").CurrentValue = true;
        await ctx.SaveChangesAsync();
        return entity.Id;
    }
}
