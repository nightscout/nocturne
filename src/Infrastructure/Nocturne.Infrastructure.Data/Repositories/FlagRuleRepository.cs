using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.EntityConfigurations;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IFlagRuleRepository"/>, one context per call, each pinned to the caller's subject. A team's rules are
/// written only by a manager of it: checked here, and by the policies on <c>flag_rules</c>.
/// </summary>
public sealed class FlagRuleRepository(INocturneDbContextFactory contexts) : IFlagRuleRepository
{
    /// <summary>
    /// The advisory lock class a create holds per subject from its count to its commit
    /// (<see cref="AdvisoryLockExtensions"/>), so two creates at once cannot each find room for one more.
    /// </summary>
    internal const int LimitLockClass = 0x4E46_4C52;

    /// <summary>The advisory lock class a team's create holds per team from its count to its commit, as <see cref="LimitLockClass"/> is per subject.</summary>
    internal const int TeamLimitLockClass = 0x4E46_4C54;

    public async Task<IReadOnlyList<FlagRule>> GetOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var rows = await OldestFirst(db.FlagRules.AsNoTracking().Where(r => r.SubjectId == subjectId)).ToListAsync(ct);
        return [.. rows.Select(ToModel)];
    }

    public async Task<FlagRule?> CreateAsync(Guid subjectId, FlagRuleDraft draft, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var row = await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(LimitLockClass, subjectId, token);
            if (await db.FlagRules.CountAsync(r => r.SubjectId == subjectId, token) >= FlagRuleLimits.PerViewer)
                return null;

            var row = new FlagRuleEntity { Id = Guid.CreateVersion7(), SubjectId = subjectId };
            Apply(row, draft);
            db.FlagRules.Add(row);
            await db.SaveChangesAsync(token);
            return row;
        }, ct: ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<FlagRule?> UpdateAsync(Guid subjectId, Guid ruleId, FlagRuleDraft draft, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var row = await db.FlagRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.SubjectId == subjectId, ct);
        if (row is null)
            return null;

        Apply(row, draft);
        await db.SaveChangesAsync(ct);
        return ToModel(row);
    }

    public async Task<bool> DeleteAsync(Guid subjectId, Guid ruleId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var row = await db.FlagRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.SubjectId == subjectId, ct);
        if (row is null)
            return false;

        db.FlagRules.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<FlagRule>?> GetOfTeamAsync(
        Guid teamId, Guid callerSubjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(callerSubjectId, ct);
        var member = await db.TeamMembers.AnyAsync(
            m => m.TeamId == teamId && m.SubjectId == callerSubjectId && (m.EndsAt == null || m.EndsAt > now), ct);
        if (!member)
            return null;

        var rows = await OldestFirst(db.FlagRules.AsNoTracking().Where(r => r.TeamId == teamId)).ToListAsync(ct);
        return [.. rows.Select(ToModel)];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<FlagRule>>> GetOfTeamsAsync(
        Guid subjectId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<FlagRule>>();

        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var ids = teamIds.ToList();
        var rows = await OldestFirst(db.FlagRules.AsNoTracking().Where(r => r.TeamId != null && ids.Contains(r.TeamId.Value)))
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.TeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FlagRule>)[.. g.Select(ToModel)]);
    }

    public async Task<TeamFlagRuleWrite> CreateForTeamAsync(
        Guid teamId, Guid callerSubjectId, FlagRuleDraft draft, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(callerSubjectId, ct);
        return await TeamRepository.AsManagerOnAsync(db, teamId, callerSubjectId, NotFound, async (_, token) =>
        {
            await db.LockForTransactionAsync(TeamLimitLockClass, teamId, token);
            if (await db.FlagRules.CountAsync(r => r.TeamId == teamId, token) >= FlagRuleLimits.PerTeam)
                return new TeamFlagRuleWrite(TeamFlagRuleChange.LimitReached, null);

            var row = new FlagRuleEntity { Id = Guid.CreateVersion7(), TeamId = teamId };
            Apply(row, draft);
            db.FlagRules.Add(row);
            TeamAudit.StageFlagRuleCreated(db, teamId, row);
            await db.SaveChangesAsync(token);
            return new TeamFlagRuleWrite(TeamFlagRuleChange.Changed, ToModel(row));
        }, ct);
    }

    public async Task<TeamFlagRuleWrite> UpdateForTeamAsync(
        Guid teamId, Guid callerSubjectId, Guid ruleId, FlagRuleDraft draft, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(callerSubjectId, ct);
        return await TeamRepository.AsManagerOnAsync(db, teamId, callerSubjectId, NotFound, async (_, token) =>
        {
            if (await TeamRowAsync(db, teamId, ruleId, token) is not { } row)
                return NotFound;

            var before = TeamAudit.FlagRuleDetails(row);
            Apply(row, draft);
            TeamAudit.StageFlagRuleChanged(db, teamId, before, row);
            await db.SaveChangesAsync(token);
            return new TeamFlagRuleWrite(TeamFlagRuleChange.Changed, ToModel(row));
        }, ct);
    }

    public async Task<TeamFlagRuleChange> DeleteForTeamAsync(
        Guid teamId, Guid callerSubjectId, Guid ruleId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(callerSubjectId, ct);
        return await TeamRepository.AsManagerOnAsync(db, teamId, callerSubjectId, TeamFlagRuleChange.NotFound, async (_, token) =>
        {
            if (await TeamRowAsync(db, teamId, ruleId, token) is not { } row)
                return TeamFlagRuleChange.NotFound;

            TeamAudit.StageFlagRuleDeleted(db, teamId, row);
            db.FlagRules.Remove(row);
            await db.SaveChangesAsync(token);
            return TeamFlagRuleChange.Changed;
        }, ct);
    }

    private static readonly TeamFlagRuleWrite NotFound = new(TeamFlagRuleChange.NotFound, null);

    /// <summary>The team's rule, tracked; null when the team holds no such rule.</summary>
    private static Task<FlagRuleEntity?> TeamRowAsync(NocturneDbContext db, Guid teamId, Guid ruleId, CancellationToken ct) =>
        db.FlagRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.TeamId == teamId, ct);

    private static IQueryable<FlagRuleEntity> OldestFirst(IQueryable<FlagRuleEntity> rows) =>
        rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id);

    private static void Apply(FlagRuleEntity row, FlagRuleDraft draft)
    {
        row.Dataset = draft.Dataset;
        row.Field = draft.Field;
        row.Days = draft.Days;
        row.Comparison = FlagRuleColumns.Of(draft.Comparison);
        row.Value = draft.Value;
        row.Level = FlagRuleColumns.Of(draft.Level);
    }

    private static FlagRule ToModel(FlagRuleEntity row) => new(
        row.Id,
        row.Dataset,
        row.Field,
        row.Days,
        FlagRuleColumns.ComparisonOf(row.Comparison),
        row.Value,
        FlagRuleColumns.LevelOf(row.Level),
        row.CreatedAt,
        row.UpdatedAt);
}
