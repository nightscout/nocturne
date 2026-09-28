using Nocturne.Core.Models.Translations;

namespace Nocturne.Core.Contracts.Translations;

/// <summary>
/// Turns a translation contribution into an upstream pull request, either
/// directly (instance has a PAT) or by relaying to nocturne.run.
/// </summary>
public interface ITranslationContributionService
{
    bool HasLocalPat { get; }

    /// <summary>Whether the anonymous relay ingress is open on this instance.</summary>
    bool AcceptsRelay { get; }
    Task<TranslationContributionResponse> SubmitAsync(TranslationContributionRequest request, CancellationToken ct);
    Task<TranslationContributionResponse> RelayAsync(TranslationContributionRequest request, CancellationToken ct);
}
