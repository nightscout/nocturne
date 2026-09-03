using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Harness for the repository delete suites that assert on the audited soft-delete path: an
/// in-memory SQLite database (<c>ExecuteUpdateAsync</c> and the audit transaction need a relational
/// provider), a seeded tenant, and readers for the row's delete state and <c>mutation_audit_log</c>.
/// The repository under test is rebuilt whenever the audit context changes, because the context is
/// a constructor dependency.
/// </summary>
public abstract class AuditedSoftDeleteTestBase<TEntity> : IDisposable
    where TEntity : class, ISoftDeletable
{
    protected static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    protected AuditedSoftDeleteTestBase()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);

        _context = _db.CreateContext();
        UseAuditContext(new UserAuditContext());
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the repository under test against <paramref name="auditContext"/>.</summary>
    protected abstract void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext);

    /// <summary>The name <c>mutation_audit_log</c> records for this entity type.</summary>
    protected abstract string AuditEntityType { get; }

    protected static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    protected void UseAuditContext(IAuditContext auditContext) =>
        CreateRepository(new TestTenantDbContextFactory(_context), auditContext);

    /// <summary>Persists <paramref name="row"/> and returns its id.</summary>
    protected Guid Add(TEntity row)
    {
        _context.Set<TEntity>().Add(row);
        _context.SaveChanges();
        return (Guid)_context.Entry(row).Property("Id").CurrentValue!;
    }

    private NocturneDbContext Verify() => _db.CreateContext();

    protected async Task<(DateTime? DeletedAt, bool DeletedByUser)> ReadDeleteStateAsync(Guid id)
    {
        await using var verify = Verify();
        var row = await verify.Set<TEntity>()
            .IgnoreQueryFilters()
            .SingleAsync(e => EF.Property<Guid>(e, "Id") == id);
        return (row.DeletedAt, (bool)verify.Entry(row).Property("DeletedByUser").CurrentValue!);
    }

    protected async Task<List<MutationAuditLogEntity>> ReadAuditLogAsync()
    {
        await using var verify = Verify();
        return await verify.Set<MutationAuditLogEntity>().ToListAsync();
    }

    protected static (int Count, string Scope) ReadSummary(string? changesJson)
    {
        using var doc = JsonDocument.Parse(changesJson!);
        return (doc.RootElement.GetProperty("count").GetInt32(),
            doc.RootElement.GetProperty("scope").GetString()!);
    }

    protected sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/data-sources/dexcom";
        public bool IsSystem => false;
    }
}
