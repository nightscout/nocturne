using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Runs the <see cref="SubjectRepository"/> delete on a unit of its own and commits it, stopping
/// nothing the subject made, for a test of the repository's own locks, refusals, membership removal
/// and scrub. A test of the stop goes through the API's <c>SubjectDeletion</c>.
/// </summary>
public static class SubjectDeletions
{
    /// <summary>The delete that refuses the last owner of a tenant.</summary>
    public static async Task<SubjectChange> DeleteAsync(
        INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, Guid subjectId, CancellationToken ct) =>
        (await OnUnitAsync(contexts, linkRoute, subjectId, evenLastOwner: false, ct)).Change;

    /// <summary>The delete that refuses no last owner.</summary>
    public static Task<SubjectDeletionOutcome> DeleteEvenLastOwnerAsync(
        INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, Guid subjectId, CancellationToken ct) =>
        OnUnitAsync(contexts, linkRoute, subjectId, evenLastOwner: true, ct);

    private static async Task<SubjectDeletionOutcome> OnUnitAsync(
        INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, Guid subjectId, bool evenLastOwner, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(contexts, ct);
        return await unit.CommitAsync(
            token => new SubjectRepository(unit, linkRoute).DeleteAsync(
                subjectId, evenLastOwner, (_, _) => Task.CompletedTask, token),
            ct: ct);
    }
}
