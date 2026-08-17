using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Storage.Internal;
using Nocturne.Infrastructure.Data;

namespace Nocturne.Tests.Shared.Infrastructure;

public static class TestDbContextFactory
{
    /// <param name="interceptors">Interceptors the behaviour under test depends on, e.g.
    /// <c>MutationAuditInterceptor</c> for anything reading the soft-delete attribution flag.</param>
    public static NocturneDbContext CreateInMemoryContext(
        string? databaseName = null, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"nocturne_tests_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptors)
            .EnableSensitiveDataLogging()
            .Options;

        var context = new NocturneDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
