namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Scoped per-request signal for per-category and history-window Row-Level Security.
/// Carries facts the <see cref="ITenantAccessor"/> does not: whether the request is an
/// anonymous public share (known pre-auth, at tenant resolution), which read-scope categories
/// and history window a share may see, and whether an authenticated member or credential is
/// clamped to the last 24 hours (both known post-auth). The DbContext factory and the
/// scoped-context registration read it to stamp the carrier properties the
/// <c>TenantConnectionInterceptor</c> turns into the <c>app.is_share</c>,
/// <c>app.visible_categories</c>, <c>app.share_full_history</c> and <c>app.history_clamped</c> GUCs.
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
    /// True when the share may see full history instead of the last 24 hours. Defaults to
    /// false, so a share whose window is never resolved is clamped (fail-closed). Only
    /// meaningful when <see cref="IsShare"/> is true.
    /// </summary>
    bool FullHistory { get; }

    /// <summary>
    /// True when this request may read only the last 24 hours of time-series data: a share
    /// without full history, or a member or credential clamped by
    /// <see cref="ClampMemberHistory"/>. A non-share whose clamp is never resolved is not
    /// clamped, so members, owners and background work read full history unless something
    /// narrows them. The single predicate every tenant-keyed cache of such data checks, since
    /// RLS narrows only database reads.
    /// </summary>
    bool IsHistoryClamped { get; }

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

    /// <summary>
    /// Sets whether the share may see full history. Called by <c>AuthenticationMiddleware</c>
    /// once the share's history window is known. Has no effect unless the request was marked
    /// as a share.
    /// </summary>
    /// <param name="fullHistory">True to lift the 24-hour clamp for this share.</param>
    void SetFullHistory(bool fullHistory);

    /// <summary>
    /// Clamps an authenticated request to the last 24 hours. Called by
    /// <c>MemberScopeMiddleware</c> once the membership and credential limits are combined.
    /// There is no way to lift it within a request.
    /// </summary>
    void ClampMemberHistory();
}
