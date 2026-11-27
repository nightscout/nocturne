using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// A viewer's saved views of the People page. A view belongs to one subject, not to any tenant, and every call reads on
/// a context pinned to that subject: the <c>subject_pin</c> policy on <c>people_views</c> admits only the subject's own
/// rows, so no other context reads any. Every write holds the subject's lock from its first read to its commit, so
/// writes at once cannot pass the limit or take one name together.
/// </summary>
public interface IPeopleViewRepository
{
    /// <summary>The subject's views, in their order.</summary>
    Task<IReadOnlyList<PeopleView>> GetOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Adds a view after the subject's last; refused at <see cref="PeopleViewLimits.PerViewer"/>, or when the name is taken.</summary>
    Task<PeopleViewWrite> CreateAsync(Guid subjectId, PeopleViewDraft draft, CancellationToken ct);

    /// <summary>Changes a view's name, query, lens and period; refused when the subject holds no such view, or another has the name.</summary>
    Task<PeopleViewWrite> UpdateAsync(Guid subjectId, Guid viewId, PeopleViewDraft draft, CancellationToken ct);

    /// <summary>Whether the subject held view <paramref name="viewId"/>, now deleted.</summary>
    Task<bool> DeleteAsync(Guid subjectId, Guid viewId, CancellationToken ct);

    /// <summary>
    /// Puts the subject's views in the order of <paramref name="order"/> and returns them so; null, and nothing
    /// changed, when it does not name each of their views exactly once.
    /// </summary>
    Task<IReadOnlyList<PeopleView>?> ReorderAsync(Guid subjectId, IReadOnlyList<Guid> order, CancellationToken ct);
}
