using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Settable <see cref="ITenantAccessor"/> for golden tests. The fixture sets the current tenant
/// before a scenario resolves a scope; the scoped <see cref="NocturneDbContext"/> and
/// <c>TenantDbContextFactory</c> both read it to stamp the RLS tenant carrier.
/// </summary>
internal sealed class TestTenantAccessor : ITenantAccessor
{
    public Guid TenantId => Context?.TenantId ?? Guid.Empty;
    public bool IsResolved => Context is not null;
    public TenantContext? Context { get; private set; }
    public void SetTenant(TenantContext context) => Context = context;
}

/// <summary>
/// System-attributed <see cref="IAuditContext"/> (no human actor), matching how connectors ingest —
/// so audited soft-deletes in the dedup paths are treated as system-initiated, as in production.
/// </summary>
internal sealed class SystemAuditContext : IAuditContext
{
    public Guid? SubjectId => null;
    public string? SubjectName => null;
    public string? AuthType => null;
    public string? IpAddress => null;
    public Guid? TokenId => null;
    public string? CorrelationId => null;
    public string? Endpoint => null;
    public bool IsSystem => true;
}
