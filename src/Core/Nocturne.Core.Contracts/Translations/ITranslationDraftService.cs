using Nocturne.Core.Models.Translations;

namespace Nocturne.Core.Contracts.Translations;

public record TranslationDraftSubmitResult
{
    public required TranslationContributionResponse Contribution { get; init; }
    /// <summary>
    /// Drafts still stored after the submit: those the contribution flow
    /// reported unmatched, plus any edited while the submit was in flight.
    /// </summary>
    public int RemainingDrafts { get; init; }
}

public class TranslationDraftLimitExceededException(string message) : Exception(message);

/// <summary>
/// Server-side storage for the current user's in-progress translations.
/// </summary>
public interface ITranslationDraftService
{
    Task<IReadOnlyList<TranslationDraft>> GetDraftsAsync(string locale, CancellationToken ct = default);

    /// <summary>
    /// Upserts drafts by (locale, context, msgid). An entry with an empty
    /// Translations list deletes the matching draft.
    /// </summary>
    Task<IReadOnlyList<TranslationDraft>> UpsertDraftsAsync(
        string locale, IReadOnlyList<TranslationEntryDto> entries, CancellationToken ct = default);

    Task<int> ClearDraftsAsync(string locale, CancellationToken ct = default);

    /// <summary>
    /// Submits all drafts for the locale as one contribution. Drafts applied
    /// upstream are deleted; drafts whose message no longer exists in the
    /// catalog, and drafts edited while the submit was in flight, are kept so
    /// the work is not lost.
    /// </summary>
    Task<TranslationDraftSubmitResult> SubmitDraftsAsync(
        string locale, ContributionContributorDto contributor, string? note, CancellationToken ct = default);
}
