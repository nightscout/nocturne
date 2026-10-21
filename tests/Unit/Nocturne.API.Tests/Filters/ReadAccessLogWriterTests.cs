using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Filters;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Filters;

[Trait("Category", "Unit")]
public class ReadAccessLogWriterTests
{
    [Fact]
    public async Task WriteAsync_AfterTheRequestScopeIsDisposed_LandsAndReadsNoRequestState()
    {
        var tenantId = Guid.CreateVersion7();
        var saves = new SaveCapture();
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString();
        services.AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(database).AddInterceptors(saves));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddLogging();
        services.AddDataServices();
        services.AddSingleton<ReadAccessLogWriter>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        ReadAccessLogWriter writer;
        await using (var request = provider.CreateAsyncScope())
        {
            var reader = request.ServiceProvider.GetRequiredService<ICategoryReadContext>();
            reader.MarkShare();
            reader.SetVisibleCategories("glucose.read");
            reader.SetFullHistory(false);
            writer = request.ServiceProvider.GetRequiredService<ReadAccessLogWriter>();
        }

        await writer.WriteAsync(new ReadAccessLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Endpoint = "GET /api/v4/entries",
            CreatedAt = DateTime.UtcNow,
        });

        var saved = saves.Contexts.Should().ContainSingle().Subject;
        saved.TenantId.Should().Be(tenantId);
        saved.IsShareContext.Should().BeFalse();
        saved.VisibleCategories.Should().BeNull();
        saved.HistoryClamped.Should().BeFalse();
        saved.AuditContext.Should().BeNull();

        await using var check = await provider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(tenantId);
        (await check.ReadAccessLog.CountAsync()).Should().Be(1);
    }

    private sealed class SaveCapture : SaveChangesInterceptor
    {
        public List<NocturneDbContext> Contexts { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context is NocturneDbContext context)
                Contexts.Add(context);
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }
}
