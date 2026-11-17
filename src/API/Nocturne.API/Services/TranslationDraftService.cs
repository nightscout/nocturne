using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Translations;
using Nocturne.Core.Models.Translations;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services;

/// <summary>
/// Server-side storage for the current user's in-progress translations.
/// Tenant isolation comes from the RLS global query filter (and TenantId is
/// auto-stamped on save); this service only scopes by subject. The database
/// enforces logical-key uniqueness (see ux_translation_drafts_logical_key in
/// the AddTranslationDrafts migration).
/// </summary>
public class TranslationDraftService(
    INocturneDbContextFactory contexts,
    IHttpContextAccessor httpContextAccessor,
    ITranslationContributionService contributionService) : ITranslationDraftService
{
    /// <summary>
    /// Generous bound: larger than the full catalog, small enough to stop
    /// unbounded accumulation of fabricated msgids.
    /// </summary>
    internal const int MaxDraftsPerSubjectPerLocale = 5000;
    internal const int MaxDraftsPerSubject = 10000;

    private Guid SubjectId => httpContextAccessor.HttpContext!.GetSubjectId()!.Value;

    public async Task<IReadOnlyList<TranslationDraft>> GetDraftsAsync(
        string locale, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var entities = await LoadForLocaleAsync(db, locale, ct);
        return entities.Select(TranslationDraftMapper.ToDomainModel).ToList();
    }

    public async Task<IReadOnlyList<TranslationDraft>> UpsertDraftsAsync(
        string locale, IReadOnlyList<TranslationEntryDto> entries, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await UpsertDraftsAsync(db, locale, entries, isRetry: false, ct);
    }

    private async Task<IReadOnlyList<TranslationDraft>> UpsertDraftsAsync(
        NocturneDbContext db,
        string locale, IReadOnlyList<TranslationEntryDto> entries, bool isRetry, CancellationToken ct)
    {
        var existing = await LoadMatchingAsync(db, locale, entries, ct);
        var byKey = existing.ToDictionary(d => (d.Context, d.MsgId));
        var now = DateTime.UtcNow;
        var added = 0;

        foreach (var entry in entries)
        {
            var key = (entry.Context ?? "", entry.MsgId);

            if (entry.Translations.Count == 0)
            {
                if (byKey.TryGetValue(key, out var toDelete))
                {
                    db.TranslationDrafts.Remove(toDelete);
                    byKey.Remove(key);
                }
                continue;
            }

            if (byKey.TryGetValue(key, out var draft))
            {
                draft.Translations = entry.Translations;
                draft.UpdatedAt = now;
            }
            else
            {
                var created = new TranslationDraftEntity
                {
                    Id = Guid.CreateVersion7(),
                    SubjectId = SubjectId,
                    Locale = locale,
                    Context = entry.Context ?? "",
                    MsgId = entry.MsgId,
                    Translations = entry.Translations,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.TranslationDrafts.Add(created);
                byKey[key] = created;
                added++;
            }
        }

        if (added > 0)
            await EnforceLimitsAsync(db, locale, added, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (!isRetry && ex.IsUniqueViolation())
        {
            // Another writer inserted one of these logical keys between the
            // read above and this insert. Re-read on a clean tracker and apply
            // the same entries again; the second pass sees the winner's row and
            // updates it. Any further rejection is not a lost race and is left
            // to surface.
            db.ChangeTracker.Clear();
            return await UpsertDraftsAsync(db, locale, entries, isRetry: true, ct);
        }

        return byKey.Values
            .OrderBy(d => d.UpdatedAt)
            .Select(TranslationDraftMapper.ToDomainModel)
            .ToList();
    }

    public async Task<int> ClearDraftsAsync(string locale, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var subjectId = SubjectId;
        var drafts = await db.TranslationDrafts
            .Where(d => d.SubjectId == subjectId && d.Locale == locale)
            .ToListAsync(ct);
        db.TranslationDrafts.RemoveRange(drafts);
        await db.SaveChangesAsync(ct);
        return drafts.Count;
    }

    public async Task<TranslationDraftSubmitResult> SubmitDraftsAsync(
        string locale, ContributionContributorDto contributor, string? note, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var drafts = await LoadForLocaleAsync(db, locale, ct);

        if (drafts.Count == 0)
            throw new ContributionRejectedException("There are no drafts to submit for this locale.");

        // Remember each draft's revision so an edit that lands while the
        // (multi-second) PR flow runs is kept instead of silently deleted.
        var snapshot = drafts.ToDictionary(d => d.Id, d => d.UpdatedAt);

        var request = new TranslationContributionRequest
        {
            Locale = locale,
            Entries = drafts.Select(d => new TranslationEntryDto
            {
                MsgId = d.MsgId,
                Context = d.Context.Length == 0 ? null : d.Context,
                Translations = d.Translations,
            }).ToList(),
            Contributor = contributor,
            Note = note,
        };

        var response = contributionService.HasLocalPat
            ? await contributionService.SubmitAsync(request, ct)
            : await contributionService.RelayAsync(request, ct);

        // Nothing above leaves pending changes, so dropping the tracked graph
        // only discards the pre-submit snapshot of these rows. Without it, EF
        // identity resolution answers the reload below from the instances
        // loaded before the PR flow started — the reload would report the
        // stale UpdatedAt and delete a draft a concurrent autosave had edited.
        db.ChangeTracker.Clear();

        // Applied drafts are done; unmatched ones (message no longer in the
        // catalog) and drafts edited mid-submit are kept.
        var unmatched = response.Unmatched
            .Select(u => (u.Context, u.MsgId))
            .ToHashSet();
        var subjectId = SubjectId;
        var current = await db.TranslationDrafts
            .Where(d => d.SubjectId == subjectId && d.Locale == locale)
            .ToListAsync(ct);
        var toDelete = current
            .Where(d => !unmatched.Contains((d.Context, d.MsgId))
                && snapshot.TryGetValue(d.Id, out var seenAt)
                && d.UpdatedAt == seenAt)
            .ToList();
        db.TranslationDrafts.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);

        return new TranslationDraftSubmitResult
        {
            Contribution = response,
            RemainingDrafts = current.Count - toDelete.Count,
        };
    }

    /// <summary>
    /// Counts what is already stored so a batch cannot push the subject past
    /// either cap. Rows the batch deletes are not discounted — the caps are a
    /// ceiling on accumulation, not an exact accounting.
    /// </summary>
    private async Task EnforceLimitsAsync(NocturneDbContext db, string locale, int added, CancellationToken ct)
    {
        var subjectId = SubjectId;
        var inLocale = await db.TranslationDrafts
            .Where(d => d.SubjectId == subjectId && d.Locale == locale)
            .CountAsync(ct);
        if (inLocale + added > MaxDraftsPerSubjectPerLocale)
            throw new TranslationDraftLimitExceededException(
                $"At most {MaxDraftsPerSubjectPerLocale} drafts per locale.");

        var inOtherLocales = await db.TranslationDrafts
            .Where(d => d.SubjectId == subjectId && d.Locale != locale)
            .CountAsync(ct);
        if (inOtherLocales + inLocale + added > MaxDraftsPerSubject)
            throw new TranslationDraftLimitExceededException(
                $"At most {MaxDraftsPerSubject} drafts in total.");
    }

    /// <summary>
    /// Loads only the rows matching the incoming (context, msgid) keys, so an
    /// autosave keystroke cannot materialize the subject's whole per-locale set
    /// (thousands of rows). The msgid set narrows the query in the database;
    /// the context is matched in memory because the unique index is on
    /// md5(msgid), not on the pair.
    /// </summary>
    private async Task<List<TranslationDraftEntity>> LoadMatchingAsync(
        NocturneDbContext db,
        string locale, IReadOnlyList<TranslationEntryDto> entries, CancellationToken ct)
    {
        var msgIds = entries.Select(e => e.MsgId).Distinct().ToList();
        if (msgIds.Count == 0)
            return [];

        var wanted = entries.Select(e => (e.Context ?? "", e.MsgId)).ToHashSet();
        var subjectId = SubjectId;
        var rows = await db.TranslationDrafts
            .Where(d => d.SubjectId == subjectId && d.Locale == locale && msgIds.Contains(d.MsgId))
            .OrderBy(d => d.UpdatedAt)
            .ToListAsync(ct);

        return [.. rows.Where(d => wanted.Contains((d.Context, d.MsgId)))];
    }

    private async Task<List<TranslationDraftEntity>> LoadForLocaleAsync(NocturneDbContext db, string locale, CancellationToken ct)
    {
        var subjectId = SubjectId;
        return await db.TranslationDrafts
            .Where(d => d.SubjectId == subjectId && d.Locale == locale)
            .OrderBy(d => d.UpdatedAt)
            .ToListAsync(ct);
    }
}
