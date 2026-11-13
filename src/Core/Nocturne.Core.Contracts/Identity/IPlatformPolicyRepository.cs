using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>Instance-wide policies a platform admin sets.</summary>
public interface IPlatformPolicyRepository
{
    /// <summary>The current policy, or the defaults when none was saved.</summary>
    Task<PlatformPolicy> GetAsync(CancellationToken ct);

    /// <summary>Replaces the policy.</summary>
    Task SaveAsync(PlatformPolicy policy, CancellationToken ct);
}
