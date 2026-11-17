using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// One DI scope's view of who is reading: the tenant, whether the request is a public share and
/// which categories and history window it may see, whether the reader is clamped to 24 hours, and
/// the audit actor. Every <see cref="NocturneDbContext"/> created in the scope holds the same
/// instance and reads it live, so a context created before authentication sees the share CSV and
/// the clamp once they are resolved. <see cref="Interceptors.TenantConnectionInterceptor"/> reads it
/// when a connection opens, and the EF tenant query filter reads it through
/// <see cref="NocturneDbContext.TenantId"/>.
/// </summary>
/// <remarks>
/// The query filter, ownership stamping and the GUCs all read these values off the context's
/// properties, so they cannot diverge between commands. Within one open connection they can: the
/// GUCs are written only when a connection opens, so a connection held open (a transaction) keeps
/// the state it opened with while the properties move on.
/// <see cref="Extensions.RlsPinningExtensions.PinTenantAsync"/> exists for that case.
/// <para>
/// <see cref="FailClosed"/> is the state of a context with no scope: no tenant, not a share, no
/// categories, no clamp, no audit actor. An empty tenant matches no tenant's rows, and a share with
/// no CSV is denied all categorized data. The clamp is fail-open by design (see
/// <see cref="ICategoryReadContext.IsHistoryClamped"/>).
/// </para>
/// </remarks>
internal sealed class TenantReadContext(
    ITenantAccessor? tenantAccessor,
    ICategoryReadContext? categoryReadContext,
    IAuditContext? auditContext)
{
    internal static readonly TenantReadContext FailClosed = new(null, null, null);

    public Guid TenantId => tenantAccessor?.IsResolved == true ? tenantAccessor.TenantId : Guid.Empty;

    public bool IsShare => categoryReadContext?.IsShare == true;

    public string? VisibleCategories => IsShare ? categoryReadContext!.VisibleCategoriesCsv : null;

    public bool ShareFullHistory => IsShare && categoryReadContext!.FullHistory;

    public bool HistoryClamped => categoryReadContext?.IsHistoryClamped == true;

    public IAuditContext? AuditContext => auditContext;
}
