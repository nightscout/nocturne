namespace Nocturne.Core.Contracts.Audit;

/// <summary>
/// Shared predicate for the code paths that append mutation audit rows (the save interceptor
/// and the bulk delete/soft-delete helpers), so they agree on what counts as an unattributed
/// mutation.
/// </summary>
public static class AuditContextExtensions
{
    /// <summary>
    /// True when a mutation carries no human actor: either an explicitly system-attributed
    /// context (<see cref="IAuditContext.IsSystem"/>), or no context at all. A null context is
    /// a background save that never populated one — an HTTP request always resolves a scoped
    /// context — and writing it produces an audit row whose actor, auth type and endpoint are
    /// all null. Such mutations are not recorded; their provenance is already on the records
    /// themselves (<c>data_source</c>).
    /// </summary>
    public static bool IsSystemMutation(this IAuditContext? auditContext)
        => auditContext is null || auditContext.IsSystem;
}
