using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>Instance-wide policies a platform admin sets.</summary>
public interface IPlatformPolicyRepository
{
    /// <summary>The current policy, or the defaults when none was saved.</summary>
    Task<PlatformPolicy> GetAsync(CancellationToken ct);

    /// <summary>
    /// Sets the switches <paramref name="update"/> names in one statement, so two updates of different
    /// switches at the same moment both land, and returns the policy after it.
    /// </summary>
    Task<PlatformPolicy> UpdateAsync(PlatformPolicyUpdate update, CancellationToken ct);
}
