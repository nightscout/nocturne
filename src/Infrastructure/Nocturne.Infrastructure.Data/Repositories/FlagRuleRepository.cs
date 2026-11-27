using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.EntityConfigurations;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary><see cref="IFlagRuleRepository"/>, one context per call, each pinned to the subject.</summary>
public sealed class FlagRuleRepository(INocturneDbContextFactory contexts) : IFlagRuleRepository
{
    /// <summary>
    /// The advisory lock class a create holds per subject from its count to its commit
    /// (<see cref="AdvisoryLockExtensions"/>), so two creates at once cannot each find room for one more.
    /// </summary>
    internal const int LimitLockClass = 0x4E46_4C52;

    public async Task<IReadOnlyList<FlagRule>> GetOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var rows = await db.FlagRules.AsNoTracking()
            .Where(r => r.SubjectId == subjectId)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
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
