using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Who a request reads as, captured as values: its tenant and its <see cref="ICategoryReadContext"/>
/// state (share, categories, full history, history clamp). Work a request does in a DI scope of its
/// own carries it there with <see cref="TenantJobScope.OpenFor(IServiceProvider, TenantReader, string)"/>,
/// because a new scope's <see cref="ICategoryReadContext"/> is a non-share with no clamp and the clamp
/// fails open. Being values, it outlives the request it was captured from.
/// </summary>
/// <remarks>
/// Outside this module one is built only from a scope (<see cref="Of(IServiceProvider)"/>).
/// <see cref="ClampedToDay"/> only adds the clamp. <see cref="ForTenant"/> keeps the share, categories
/// and clamp but replaces the tenant, so its caller must have decided the reader may see that tenant,
/// as a membership check does.
/// </remarks>
public sealed class TenantReader
{
    private TenantReader(
        TenantContext? tenant, bool isShare, string? visibleCategoriesCsv, bool fullHistory, bool historyClamped)
    {
        Tenant = tenant;
        IsShare = isShare;
        VisibleCategoriesCsv = visibleCategoriesCsv;
        FullHistory = fullHistory;
        HistoryClamped = historyClamped;
    }

    public TenantContext? Tenant { get; }

    public bool IsShare { get; }

    public string? VisibleCategoriesCsv { get; }

    public bool FullHistory { get; }

    public bool HistoryClamped { get; }

    /// <summary>The reader of a request scope: its resolved tenant, if any, and its read context.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="scope"/> is the root provider,
    /// whose scoped services belong to no request.</exception>
    public static TenantReader Of(IServiceProvider scope)
    {
        if (scope.GetService<RootServiceProvider>() is { } root && ReferenceEquals(scope, root.Root))
        {
            throw new InvalidOperationException(
                "A reader is captured from a request's scope; the root provider reads as no one.");
        }

        var accessor = scope.GetRequiredService<ITenantAccessor>();
        return Of(accessor.IsResolved ? accessor.Context : null, scope.GetRequiredService<ICategoryReadContext>());
    }

    /// <summary>The reader of <paramref name="tenant"/> narrowed by <paramref name="readContext"/>.</summary>
    internal static TenantReader Of(TenantContext? tenant, ICategoryReadContext readContext) => new(
        tenant,
        readContext.IsShare,
        readContext.IsShare ? readContext.VisibleCategoriesCsv : null,
        readContext.IsShare && readContext.FullHistory,
        readContext.IsHistoryClamped);

    /// <summary>A job's reader: no share and no clamp. Only <see cref="TenantJobScope.Open(IServiceProvider, TenantContext?, string)"/> builds it.</summary>
    internal static TenantReader NoOne(TenantContext? tenant) => new(tenant, false, null, false, false);

    /// <summary>The same reader on <paramref name="tenant"/>.</summary>
    public TenantReader ForTenant(TenantContext tenant) =>
        new(tenant, IsShare, VisibleCategoriesCsv, FullHistory, HistoryClamped);

    /// <summary>The same reader limited to the last 24 hours when <paramref name="clamp"/> is true.</summary>
    public TenantReader ClampedToDay(bool clamp = true) =>
        clamp ? new(Tenant, IsShare, VisibleCategoriesCsv, FullHistory, historyClamped: true) : this;

    /// <summary>
    /// Whether <paramref name="scope"/> still reads as no one: no tenant resolved, not a share, not
    /// clamped. Every scope starts so, until middleware or <see cref="CarryInto"/> narrows it.
    /// </summary>
    public static bool ReadsAsNoOne(IServiceProvider scope) =>
        scope.GetService<ITenantAccessor>()?.IsResolved != true
        && scope.GetService<ICategoryReadContext>() is null or { IsShare: false, IsHistoryClamped: false };

    /// <summary>
    /// Sets this reader on a scope opened for it that still <see cref="ReadsAsNoOne"/>, such as a
    /// SignalR invocation scope.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="scope"/> already reads as someone:
    /// setting the tenant there would move every context of that scope.</exception>
    public void CarryInto(IServiceProvider scope)
    {
        if (!ReadsAsNoOne(scope))
            throw new InvalidOperationException("A reader is carried only into a scope that reads as no one.");

        if (Tenant is not null)
            scope.GetRequiredService<ITenantAccessor>().SetTenant(Tenant);

        if (!IsShare && !HistoryClamped)
            return;

        var readContext = scope.GetRequiredService<ICategoryReadContext>();
        if (IsShare)
        {
            readContext.MarkShare();
            if (VisibleCategoriesCsv is not null)
                readContext.SetVisibleCategories(VisibleCategoriesCsv);
            readContext.SetFullHistory(FullHistory);
        }

        if (HistoryClamped)
            readContext.ClampMemberHistory();
    }
}
