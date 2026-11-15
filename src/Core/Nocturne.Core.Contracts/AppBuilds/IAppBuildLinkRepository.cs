using Nocturne.Core.Models.AppBuilds;

namespace Nocturne.Core.Contracts.AppBuilds;

/// <summary>The resolved tenant's links to GitHub App installations, one per build target.</summary>
public interface IAppBuildLinkRepository
{
    Task<IReadOnlyList<AppBuildLink>> GetAllAsync(CancellationToken ct);

    Task<AppBuildLink?> GetAsync(string targetId, CancellationToken ct);

    /// <summary>Creates the target's link, or replaces the one it has.</summary>
    Task SaveAsync(AppBuildLink link, CancellationToken ct);

    /// <returns>False when the target had no link.</returns>
    Task<bool> SetRepositoryAsync(string targetId, string repositoryFullName, CancellationToken ct);

    /// <returns>False when the target had no link.</returns>
    Task<bool> DeleteAsync(string targetId, CancellationToken ct);
}
