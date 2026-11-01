using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// A tenant a helper set up for someone else. Only <see cref="TenantRecord.HandedOverAt"/> is
/// stored; who the helper is and whether the recipient joined are read from the members.
/// </summary>
public static class Handover
{
    /// <summary>
    /// The onboarder, which is the tenant's first person member. Not filtered by role: a helper
    /// who stepped down is still the helper.
    /// </summary>
    /// <remarks>
    /// Read from the current members, not stored: once the helper's membership is removed or their
    /// subject deactivated, the next earliest person member reads as the helper.
    /// </remarks>
    public static async Task<TenantMemberRecord?> HelperAsync(
        ITenantMembershipRepository members, Guid tenantId, CancellationToken ct) =>
        (await members.ListPeopleAsync(tenantId, ct)).FirstOrDefault();

    public static Task<bool> RecipientJoinedAsync(
        ITenantMembershipRepository members, Guid tenantId, TenantMemberRecord? helper, CancellationToken ct) =>
        helper is null
            ? Task.FromResult(false)
            : members.AnyOtherOwnerAsync(tenantId, helper.Id, ct);

    public static async Task<SetupHubHandover?> DescribeAsync(
        ITenantMembershipRepository members,
        Guid tenantId,
        PatientRelationship? relationship,
        DateTime? handedOverAt,
        Guid? callerId,
        CancellationToken ct)
    {
        if (relationship != PatientRelationship.Helper && handedOverAt is null)
            return null;

        if (await HelperAsync(members, tenantId, ct) is not { } helper)
            return null;

        var callerIsHelper = callerId == helper.SubjectId;
        var recipientJoined = await RecipientJoinedAsync(members, tenantId, helper, ct);

        return new SetupHubHandover(
            helper.SubjectName ?? string.Empty,
            callerIsHelper,
            recipientJoined,
            relationship != PatientRelationship.Helper,
            callerIsHelper && helper.IsOwner && recipientJoined);
    }
}
