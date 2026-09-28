using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Scoped, request-lifetime implementation of <see cref="ICategoryReadContext"/>.
/// A plain mutable holder: the middleware pipeline writes it (share marker pre-auth,
/// CSV and history clamp post-auth) and the DbContext factory reads it during query execution. There is
/// no concurrent access within a request: every write happens in the middleware pipeline
/// before any controller or repository runs.
/// </summary>
public sealed class CategoryReadContext : ICategoryReadContext
{
    public bool IsShare { get; private set; }

    public string? VisibleCategoriesCsv { get; private set; }

    public bool FullHistory { get; private set; }

    private bool _memberHistoryClamped;

    public bool IsHistoryClamped => (IsShare && !FullHistory) || _memberHistoryClamped;

    public void MarkShare() => IsShare = true;

    public void SetVisibleCategories(string csv)
    {
        if (IsShare)
        {
            VisibleCategoriesCsv = csv ?? string.Empty;
        }
    }

    public void SetFullHistory(bool fullHistory)
    {
        if (IsShare)
        {
            FullHistory = fullHistory;
        }
    }

    public void ClampMemberHistory() => _memberHistoryClamped = true;
}
