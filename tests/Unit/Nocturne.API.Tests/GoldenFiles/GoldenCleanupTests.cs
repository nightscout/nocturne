using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.GoldenFiles;

public class GoldenCleanupTests : GoldenFileTestBase
{
    public GoldenCleanupTests(GoldenFileWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task Cleanup_EmptiesARecordTableNoGoldenWritesYet()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
                .CreateForTenantAsync(GoldenTenantId);
            db.Notes.Add(new NoteEntity { Id = Guid.NewGuid(), TenantId = GoldenTenantId, Timestamp = DateTime.UtcNow, Text = "left behind" });
            db.HeartRates.Add(new HeartRateEntity { Id = Guid.NewGuid(), TenantId = GoldenTenantId, Timestamp = DateTime.UtcNow, Bpm = 60 });
            await db.SaveChangesAsync();
        }

        await InitializeAsync();

        using var check = Factory.Services.CreateScope();
        await using var after = await check.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(GoldenTenantId);
        (await after.Notes.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await after.HeartRates.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
