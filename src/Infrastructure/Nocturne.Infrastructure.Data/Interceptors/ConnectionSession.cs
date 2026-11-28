namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// The reader state <see cref="TenantConnectionInterceptor"/> writes to a connection's session when a context opens it.
/// Two contexts with equal sessions read the same rows on one connection.
/// </summary>
internal readonly record struct ConnectionSession(
    Guid TenantId,
    Guid SubjectId,
    bool IsShare,
    string? VisibleCategories,
    bool ShareFullHistory,
    bool HistoryClamped)
{
    public static ConnectionSession Of(NocturneDbContext context) => new(
        context.TenantId,
        context.SubjectId,
        context.IsShareContext,
        context.VisibleCategories,
        context.ShareFullHistory,
        context.HistoryClamped);
}
