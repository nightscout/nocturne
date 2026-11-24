using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// The <c>admits</c> callbacks of <see cref="ILoginCodeService.RedeemAsync"/>. A code is a bearer
/// link that outlives the moment it was minted, so a redemption re-reads what it was minted on.
/// </summary>
public static class LoginCodeAdmission
{
    /// <summary>
    /// Admits a code only while its subject is a live member of <paramref name="tenantId"/>
    /// (<see cref="ITenantMemberService.IsMemberAsync"/>), so a member whose access has ended is
    /// refused as an unknown code is.
    /// </summary>
    public static Func<RedeemedLoginCode, CancellationToken, Task<bool>> LiveMemberOf(
        ITenantMemberService tenantMembers, Guid tenantId) =>
        (code, ct) => tenantMembers.IsMemberAsync(code.SubjectId, tenantId, ct);
}
