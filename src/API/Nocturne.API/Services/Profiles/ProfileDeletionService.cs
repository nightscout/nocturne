using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Profiles;

/// <inheritdoc />
public class ProfileDeletionService : IProfileDeletionService
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<ProfileDeletionService> _logger;

    /// <summary>Rows read to evaluate the guard, matching the ceiling the summary endpoint reads at.</summary>
    private const int GuardReadLimit = 1000;

    /// <summary>
    /// Initializes a new instance of <see cref="ProfileDeletionService"/>.
    /// </summary>
    /// <param name="contextFactory">Creates the tenant-scoped context the cascade runs on.</param>
    /// <param name="therapyRepo">Read side of the guard.</param>
    /// <param name="auditContext">
    /// Attribution for the delete. It decides <c>deleted_by_user</c>, and so whether a later resync
    /// may re-create the rows — see <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{T}"/>.
    /// </param>
    /// <param name="logger">Logger.</param>
    public ProfileDeletionService(
        ITenantDbContextFactory contextFactory,
        ITherapySettingsRepository therapyRepo,
        IAuditContext auditContext,
        ILogger<ProfileDeletionService> logger
    )
    {
        _contextFactory = contextFactory;
        _therapyRepo = therapyRepo;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProfileDeletionResult> DeleteByProfileNameAsync(
        string profileName,
        CancellationToken ct = default
    )
    {
        var settings = await _therapyRepo.GetAsync(
            null,
            null,
            null,
            null,
            GuardReadLimit,
            0,
            true,
            ct
        );

        var refusal = IProfileDeletionService.Evaluate(settings, profileName);
        if (refusal != ProfileDeletionRefusal.None)
            return new ProfileDeletionResult(refusal, 0);

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var scope = $"profile_name={profileName}";

        var deleted = await ctx.ExecuteInTransactionAsync(
            async token =>
                await ctx.AuditedSoftDeleteAsync(
                    ctx.BasalSchedules.Where(e => e.ProfileName == profileName),
                    _auditContext, scope, token)
                + await ctx.AuditedSoftDeleteAsync(
                    ctx.CarbRatioSchedules.Where(e => e.ProfileName == profileName),
                    _auditContext, scope, token)
                + await ctx.AuditedSoftDeleteAsync(
                    ctx.SensitivitySchedules.Where(e => e.ProfileName == profileName),
                    _auditContext, scope, token)
                + await ctx.AuditedSoftDeleteAsync(
                    ctx.TargetRangeSchedules.Where(e => e.ProfileName == profileName),
                    _auditContext, scope, token)
                // Last: the projection keys on therapy settings, so while this row survives the
                // profile still resolves. A roll-back therefore cannot leave a name whose schedules
                // are gone but which every legacy consumer still reads as present.
                + await ctx.AuditedSoftDeleteAsync(
                    ctx.TherapySettings.Where(e => e.ProfileName == profileName),
                    _auditContext, scope, token),
            ct: ct);

        _logger.LogInformation(
            "Deleted therapy profile {ProfileName}: {Count} records across five tables",
            profileName,
            deleted
        );

        return new ProfileDeletionResult(ProfileDeletionRefusal.None, deleted);
    }
}
