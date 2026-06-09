namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Scoped per-request signal for public-share per-category Row-Level Security.
/// Carries two facts the <see cref="ITenantAccessor"/> does not: whether the request is
/// an anonymous public share (known pre-auth, at tenant resolution) and, if so, which
/// read-scope categories it may see (known post-auth, after the share's public scopes are
/// resolved). The DbContext factory and the scoped-context registration read it to stamp
/// the carrier properties the <c>TenantConnectionInterceptor</c> turns into the
/// <c>app.is_share</c> and <c>app.visible_categories</c> GUCs.
/// </summary>
public interface ICategoryReadContext
{
    /// <summary>
    /// True when the request arrived via a public share token. Set pre-auth so it is
    /// reliably present on every connection a share opens; a share connection that reaches
    /// a categorized table without a resolved CSV is denied (fail-closed), never opened up.
    /// </summary>
    bool IsShare { get; }

    /// <summary>
    /// Comma-separated governing read scopes the share may see, or <c>null</c> until
    /// resolved. Only meaningful when <see cref="IsShare"/> is true.
    /// </summary>
    string? VisibleCategoriesCsv { get; }

    /// <summary>
    /// Marks the request as an anonymous public share. Called by
    /// <c>TenantResolutionMiddleware</c> before the scoped context is pinned.
    /// </summary>
    void MarkShare();

    /// <summary>
    /// Sets the resolved visible-categories CSV. Called by <c>AuthenticationMiddleware</c>
    /// once the share's public scopes are known. Has no effect unless the request was
    /// marked as a share.
    /// </summary>
    /// <param name="csv">The comma-separated governing read scopes (may be empty).</param>
    void SetVisibleCategories(string csv);
}
