using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Migration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout migration re-run meets the foods an earlier run imported; what it may do to them
/// follows the connector import's rule for deleted rows.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationFoodImportTests
{
    private const string OriginalId = "65f0000000000000000000f1";

    private readonly DbContextOptions<NocturneDbContext> _options = new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase($"migration_food_import_{Guid.NewGuid()}")
        .Options;

    private readonly Guid _tenantId = Guid.NewGuid();

    private NocturneDbContext NewContext() => new(_options) { TenantId = _tenantId };

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task FoodImportBlocked_BlocksALiveOrUserDeletedFood_NotASystemSweptOne(bool? deletedByUser, bool blocked)
    {
        await using (var context = NewContext())
        {
            var food = new FoodEntity
            {
                Id = Guid.CreateVersion7(), OriginalId = OriginalId, Type = "food", Name = "Oats", Carbs = 30,
            };
            context.Foods.Add(food);
            if (deletedByUser is { } byUser)
            {
                food.DeletedAt = DateTime.UtcNow;
                context.Entry(food).Property("DeletedByUser").CurrentValue = byUser;
            }

            await context.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        (await MigrationJob.FoodImportBlockedAsync(readContext, OriginalId, "Oats", "food", CancellationToken.None))
            .Should().Be(blocked);
        (await MigrationJob.FoodImportBlockedAsync(readContext, null, "Oats", "food", CancellationToken.None))
            .Should().Be(blocked, "a food without an id is matched by name and type");
    }

    private sealed class FoodSource(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/api/v1/food.json"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData(true, $$"""[{"_id":"{{OriginalId}}","name":"Renamed oats","type":"food"}]""", 0)]
    [InlineData(true, """[{"name":"Oats","type":"food"}]""", 0)]
    [InlineData(false, $$"""[{"_id":"{{OriginalId}}","name":"Oats","type":"food"}]""", 1)]
    public async Task An_api_migration_rerun_does_not_recreate_a_food_the_user_deleted(
        bool deletedByUser, string source, int liveAfter)
    {
        await using var provider = MigrationJobHarness.BuildProvider(new FoodSource(source));
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            context.TenantId = _tenantId;
            var food = new FoodEntity
            {
                Id = Guid.CreateVersion7(), OriginalId = OriginalId, Type = "food", Name = "Oats", Carbs = 30,
                DeletedAt = DateTime.UtcNow,
            };
            context.Foods.Add(food);
            context.Entry(food).Property("DeletedByUser").CurrentValue = deletedByUser;
            await context.SaveChangesAsync();
        }

        var status = await MigrationJobHarness.RunAsync(provider, onCreated: null, ["food"], tenantId: _tenantId);

        status.CollectionProgress["food"].DocumentsFailed.Should().Be(0);
        using var readScope = provider.CreateScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        readContext.TenantId = _tenantId;
        (await readContext.Foods.CountAsync()).Should().Be(liveAfter);
    }
}
