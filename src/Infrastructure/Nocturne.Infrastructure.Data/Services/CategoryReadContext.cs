using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Scoped, request-lifetime implementation of <see cref="ICategoryReadContext"/>.
/// A plain mutable holder: the middleware pipeline writes it (share marker pre-auth,
/// CSV post-auth) and the DbContext factory reads it during query execution. There is
/// no concurrent access within a request — both writes happen in the middleware pipeline
/// before any controller or repository runs.
/// </summary>
public sealed class CategoryReadContext : ICategoryReadContext
{
    public bool IsShare { get; private set; }

    public string? VisibleCategoriesCsv { get; private set; }

    public void MarkShare() => IsShare = true;

    public void SetVisibleCategories(string csv)
    {
        if (IsShare)
        {
            VisibleCategoriesCsv = csv ?? string.Empty;
        }
    }
}
