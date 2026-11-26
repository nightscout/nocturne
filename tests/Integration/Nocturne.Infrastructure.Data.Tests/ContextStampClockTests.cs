using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A context stamps <c>created_at</c>, <c>updated_at</c>, <c>sys_created_at</c>, <c>sys_updated_at</c>
/// and its audit rows from the <see cref="TimeProvider"/> registered beside the factory that made it,
/// so a service that compares a stamp with its own clock reads the same one.
/// </summary>
[Trait("Category", "Integration")]
public class ContextStampClockTests(ContextStampClockTests.Database database)
    : IClassFixture<ContextStampClockTests.Database>
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234);

    public sealed class Database() : MigratedDatabase("context_stamp_clock");

    private static readonly IAuditContext Actor = new TestAuditActor { AuthType = "ApiKey" };

    [Fact]
    public async Task A_scoped_context_stamps_every_column_and_the_audit_row_from_the_registered_clock()
    {
        var tenant = await SeedTenantAsync();
        var clock = new FakeTimeProvider(Start);
        await using var provider = TestContexts.BuildProvider(database.AppConnectionString, configure: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped(_ => Actor);
        });
        var created = NocturneDbContext.AtStoredPrecision(Start.UtcDateTime);
        var foodId = Guid.NewGuid();
        var spanId = Guid.NewGuid();

        await using (var scope = provider.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
                .CreateForTenantAsync(tenant);
            db.Foods.Add(new FoodEntity { Id = foodId, Name = "toast" });
            db.StateSpans.Add(new StateSpanEntity
            {
                Id = spanId, Category = "Profile", State = "running", StartTimestamp = created.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromHours(3));
        var modified = NocturneDbContext.AtStoredPrecision(Start.AddHours(3).UtcDateTime);
        await using (var scope = provider.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
                .CreateForTenantAsync(tenant);
            (await db.Foods.SingleAsync(f => f.Id == foodId)).Name = "toast and jam";
            (await db.StateSpans.SingleAsync(s => s.Id == spanId)).State = "ended";
            await db.SaveChangesAsync();
        }

        await using var read = provider.CreateAsyncScope();
        await using var check = await read.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(tenant);
        var food = await check.Foods.AsNoTracking().SingleAsync(f => f.Id == foodId);
        var span = await check.StateSpans.AsNoTracking().SingleAsync(s => s.Id == spanId);
        food.SysCreatedAt.Should().Be(created);
        food.SysUpdatedAt.Should().Be(modified);
        span.CreatedAt.Should().Be(created);
        span.UpdatedAt.Should().Be(modified);

        var audit = await check.MutationAuditLog.AsNoTracking()
            .Where(a => a.EntityId == foodId || a.EntityId == spanId)
            .ToListAsync();
        audit.Where(a => a.Action == "create").Should().OnlyContain(a => a.CreatedAt == created).And.HaveCount(2);
        audit.Where(a => a.Action == "update").Should().OnlyContain(a => a.CreatedAt == modified).And.HaveCount(2);
    }

    [Fact]
    public async Task A_context_of_the_root_factory_stamps_with_the_system_clock()
    {
        var tenant = await SeedTenantAsync();
        await using var provider = TestContexts.BuildProvider(database.AppConnectionString);
        var foodId = Guid.NewGuid();

        var before = DateTime.UtcNow;
        await using (var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant))
        {
            db.Foods.Add(new FoodEntity { Id = foodId, Name = "toast" });
            await db.SaveChangesAsync();
        }
        var after = DateTime.UtcNow;

        await using var check = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        var stamped = (await check.Foods.AsNoTracking().SingleAsync(f => f.Id == foodId)).SysCreatedAt;
        stamped.Should().BeOnOrAfter(NocturneDbContext.AtStoredPrecision(before)).And.BeOnOrBefore(after);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.SeedTenantAsync(tenant, $"stamp-clock-{tenant:N}", "context-stamp-clock");
        return tenant;
    }
}
