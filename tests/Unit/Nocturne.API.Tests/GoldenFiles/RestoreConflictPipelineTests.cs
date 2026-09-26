using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Tests.GoldenFiles;

/// <summary>
/// The whole request pipeline for a restore that would clash with a live row's sync key: a 409
/// through <see cref="API.Filters.RecreationBlockedFilter"/> for one record, and a per-id conflict
/// list for many.
/// </summary>
public class RestoreConflictPipelineTests : GoldenFileTestBase
{
    private const string DataSource = "aaps";

    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime T0 = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    public RestoreConflictPipelineTests(GoldenFileWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task V4RestoreBolus_WhenALiveRowHoldsTheSyncKey_Answers409_AndLeavesItDeleted()
    {
        var deleted = await SeedBolusAsync("restore-sync-1", deletedAt: T0.AddHours(1));
        var live = await SeedBolusAsync("restore-sync-1", deletedAt: null);

        var response = await Client.PostAsync($"/api/v4/insulin/boluses/{deleted}/restore", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Contain("newer version").And.Contain("restore-sync-1");

        await using var verify = NewContext();
        var rows = await verify.Boluses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.SyncIdentifier == "restore-sync-1").ToListAsync();
        rows.Single(b => b.Id == deleted).DeletedAt.Should().NotBeNull();
        rows.Single(b => b.Id == live).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task V4BulkRestoreBoluses_RestoresTheCleanIds_AndNamesTheConflictingOnes()
    {
        var clashing = await SeedBolusAsync("restore-sync-2", deletedAt: T0.AddHours(1));
        await SeedBolusAsync("restore-sync-2", deletedAt: null);
        var clean = await SeedBolusAsync("restore-sync-3", deletedAt: T0.AddHours(1));

        var response = await Client.PostAsJsonAsync("/api/v4/insulin/boluses/restore", new[] { clashing, clean });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BulkRestoreResult<Bolus>>();
        result!.Restored.Select(b => b.Id).Should().Equal(clean);
        result.Conflicts.Should().Equal(clashing);

        await using var verify = NewContext();
        (await verify.Boluses.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == clashing))
            .DeletedAt.Should().NotBeNull();
        (await verify.Boluses.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == clean))
            .DeletedAt.Should().BeNull();
    }

    private NocturneDbContext NewContext()
    {
        var factory = Factory.Services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        var ctx = factory.CreateDbContext();
        ctx.TenantId = TestTenantId;
        return ctx;
    }

    private async Task<Guid> SeedBolusAsync(string syncIdentifier, DateTime? deletedAt)
    {
        await using var ctx = NewContext();
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = T0,
            DataSource = DataSource,
            SyncIdentifier = syncIdentifier,
            Insulin = 5.0,
            DeletedAt = deletedAt,
        };
        ctx.Boluses.Add(entity);
        await ctx.SaveChangesAsync();
        return entity.Id;
    }
}
