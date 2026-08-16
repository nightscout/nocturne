using Nocturne.Core.Models.Content;

namespace Nocturne.Core.Contracts.Content;

/// <summary>
/// Turns a CMS content contribution (blog/docs .svx) into an upstream pull
/// request, either directly (instance has a PAT) or by relaying to
/// nocturne.run — the same split as translation contributions.
/// </summary>
public interface IContentContributionService
{
    bool HasLocalPat { get; }

    /// <summary>Whether the anonymous relay ingress is open on this instance.</summary>
    bool AcceptsRelay { get; }
    Task<ContentContributionResponse> SubmitAsync(ContentContributionRequest request, CancellationToken ct);
    Task<ContentContributionResponse> RelayAsync(ContentContributionRequest request, CancellationToken ct);
}
