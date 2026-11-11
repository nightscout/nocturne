using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Writes alert rules and channels on its own context per call, saving each, or on the caller's
/// <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
public class AlertRuleRepository : IAlertRuleRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;
    private readonly TimeProvider _time;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public AlertRuleRepository(INocturneDbContextFactory contexts, TimeProvider? time = null)
    {
        _contexts = contexts;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it, through <c>AlertRuleRearm</c> when the
    /// change invalidates a re-arm hold, so the save and the hold's clear land in one transaction.
    /// A read tracks its rows, so a write on the same unit edits the rows it read in place: no row
    /// is loaded twice, and only the columns the write changed are saved.
    /// </summary>
    public AlertRuleRepository(DbUnitOfWork unit, TimeProvider? time = null)
    {
        _unit = unit;
        _time = time ?? TimeProvider.System;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private async ValueTask<NocturneDbContext> OpenForTenantAsync(Guid tenantId, CancellationToken ct) =>
        await (_contexts ?? throw new InvalidOperationException(
                "A repository over a unit of work runs on the unit's tenant and cannot pin another."))
            .CreateForTenantAsync(tenantId, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewAlertRule rule, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = NewEntity(db.TenantId, rule, Now);
        db.AlertRules.Add(entity);
        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> UpdateAsync(Guid ruleId, AlertRuleWrite rule, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = await LoadAsync(db, ruleId, withChannels: false, ct);
        if (entity is null)
            return false;

        Apply(entity, rule);
        entity.UpdatedAt = Now;
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The replaced rows are deleted. The foreign key from deliveries to channels is SetNull, not
    /// Cascade, so the deliveries a replaced channel made stay as the audit trail of what was sent.
    /// </remarks>
    public virtual async Task<bool> ReplaceChannelsAsync(
        Guid ruleId,
        IReadOnlyList<AlertRuleChannelWrite> channels,
        Func<AlertRuleChannelSnapshot, bool>? kept,
        CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = await LoadAsync(db, ruleId, withChannels: true, ct);
        if (entity is null)
            return false;

        var retained = RetainedSecrets(entity.Channels);
        var keptRows = entity.Channels
            .Where(c => kept is not null && kept(AlertRuleMapper.ToSnapshot(c)))
            .OrderBy(c => c.SortOrder)
            .ToList();
        foreach (var replaced in entity.Channels.Except(keptRows).ToList())
        {
            entity.Channels.Remove(replaced);
            db.AlertRuleChannels.Remove(replaced);
        }

        var sortOrder = 0;
        var now = Now;
        foreach (var channel in channels)
            entity.Channels.Add(NewChannel(entity, channel, sortOrder++, retained, now));
        foreach (var keptRow in keptRows)
            keptRow.SortOrder = sortOrder++;

        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AddChannelAsync(Guid ruleId, AlertRuleChannelWrite channel, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = await LoadAsync(db, ruleId, withChannels: true, ct);
        if (entity is null)
            return false;

        entity.Channels.Add(NewChannel(entity, channel, entity.Channels.Count, RetainedSecrets(entity.Channels), Now));
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RemoveChannelAsync(Guid ruleId, Guid channelId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = await LoadAsync(db, ruleId, withChannels: true, ct);
        var channel = entity?.Channels.FirstOrDefault(c => c.Id == channelId);
        if (entity is null || channel is null)
            return false;

        entity.Channels.Remove(channel);
        db.AlertRuleChannels.Remove(channel);
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool?> ToggleEnabledAsync(Guid ruleId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = await LoadAsync(db, ruleId, withChannels: false, ct);
        if (entity is null)
            return null;

        var wasEnabled = entity.IsEnabled;
        entity.IsEnabled = !wasEnabled;
        entity.UpdatedAt = Now;
        await SaveAsync(db, ct);
        return wasEnabled;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> DeleteAsync(Guid ruleId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var entity = await LoadAsync(db, ruleId, withChannels: false, ct);
        if (entity is null)
            return false;

        db.AlertRules.Remove(entity);
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyDictionary<Guid, AlertRuleRecord>> GetManagedRulesAsync(
        string managedBy, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var rules = await RulesWithChannels(lease.Context)
            .Where(r => r.ManagedBy == managedBy)
            .ToListAsync(ct);
        return rules.ToDictionary(r => r.Id, AlertRuleMapper.ToRecord);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyDictionary<StarterAlertKind, AlertRuleRecord>> GetStarterRulesAsync(
        CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var rules = await RulesWithChannels(lease.Context)
            .Where(r => r.StarterKind != null)
            .ToListAsync(ct);
        return rules
            .GroupBy(r => r.StarterKind!.Value)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => AlertRuleMapper.ToRecord(g.OrderBy(r => r.CreatedAt).First()));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>>> GetChannelsAsync(
        IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var ids = ruleIds.ToList();
        var channels = await lease.Context.AlertRuleChannels
            .AsNoTracking()
            .Where(c => ids.Contains(c.AlertRuleId))
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        return channels
            .GroupBy(c => c.AlertRuleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AlertRuleChannelSnapshot>)g.Select(AlertRuleMapper.ToSnapshot).ToList());
    }

    /// <inheritdoc/>
    public virtual async Task<bool> ChannelExistsAsync(Guid channelId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        return await lease.Context.AlertRuleChannels.AnyAsync(c => c.Id == channelId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<AlertChannelSecret>> GetChannelSecretsAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        return await lease.Context.AlertRuleChannels
            .AsNoTracking()
            .Where(c => c.Secret != null)
            .OrderBy(c => c.AlertRule!.Name).ThenBy(c => c.SortOrder)
            .Select(c => new AlertChannelSecret(c.AlertRuleId, c.AlertRule!.Name, c.Destination, c.DestinationLabel, c.Secret!))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlySet<string>> GetInAppDestinationsAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var destinations = await lease.Context.AlertRuleChannels
            .AsNoTracking()
            .Where(c => c.ChannelType == ChannelType.InApp)
            .Select(c => c.Destination)
            .Distinct()
            .ToListAsync(ct);
        return destinations.ToHashSet();
    }

    /// <inheritdoc/>
    public virtual async Task<int> SetScopeClassAsync(
        Guid tenantId, RuleScopeClass scopeClass, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);

        var ids = ruleIds.ToList();
        return await db.AlertRules
            .Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ScopeClass, scopeClass), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<SeededAlertRules> CreateMissingByNameAsync(
        IReadOnlyList<NewAlertRule> rules, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var idsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        foreach (var rule in rules)
        {
            var name = rule.Rule.Name;
            var existing = await db.AlertRules.FirstOrDefaultAsync(r => r.Name == name, ct);
            if (existing is null)
            {
                existing = NewEntity(db.TenantId, rule, Now);
                db.AlertRules.Add(existing);
                created++;
            }

            idsByName[name] = existing.Id;
        }

        await SaveAsync(db, ct);
        return new SeededAlertRules(idsByName, created);
    }

    private IQueryable<AlertRuleEntity> RulesWithChannels(NocturneDbContext db) =>
        _unit is null
            ? db.AlertRules.AsNoTracking().Include(r => r.Channels)
            : db.AlertRules.Include(r => r.Channels);

    /// <summary>
    /// The rule as tracked, from the context first so a rule this unit has staged is found, with its
    /// channels loaded when asked for.
    /// </summary>
    private static async Task<AlertRuleEntity?> LoadAsync(
        NocturneDbContext db, Guid ruleId, bool withChannels, CancellationToken ct)
    {
        var rule = db.AlertRules.Local.FirstOrDefault(r => r.Id == ruleId)
            ?? await db.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);
        if (rule is null || !withChannels)
            return rule;

        var channels = db.Entry(rule).Collection(r => r.Channels);
        if (!channels.IsLoaded && db.Entry(rule).State != EntityState.Added)
            await channels.LoadAsync(ct);
        return rule;
    }

    private static AlertRuleEntity NewEntity(Guid tenantId, NewAlertRule rule, DateTime now)
    {
        var entity = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ManagedBy = rule.ManagedBy,
            StarterKind = rule.StarterKind,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(entity, rule.Rule);

        var sortOrder = 0;
        foreach (var channel in rule.Channels)
            entity.Channels.Add(NewChannel(entity, channel, sortOrder++, NoRetainedSecrets, now));
        return entity;
    }

    private static void Apply(AlertRuleEntity entity, AlertRuleWrite rule)
    {
        entity.Name = rule.Name;
        entity.Description = rule.Description;
        entity.ConditionType = rule.ConditionType;
        entity.ConditionParams = rule.ConditionParams;
        entity.ScopeClass = rule.ScopeClass;
        entity.Severity = rule.Severity;
        entity.IsEnabled = rule.IsEnabled;
        entity.SortOrder = rule.SortOrder;
        entity.AllowThroughDnd = rule.AllowThroughDnd;
        entity.AutoResolveEnabled = rule.AutoResolveEnabled;
        entity.AutoResolveParams = rule.AutoResolveParams;
        entity.ClientConfiguration = rule.ClientConfiguration;
        entity.ReviewReason = rule.ReviewReason;
    }

    private static AlertRuleChannelEntity NewChannel(
        AlertRuleEntity rule,
        AlertRuleChannelWrite channel,
        int sortOrder,
        IReadOnlyDictionary<(ChannelType, string), string> retainedSecrets,
        DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = rule.TenantId,
        AlertRuleId = rule.Id,
        ChannelType = channel.ChannelType,
        Destination = channel.Destination,
        DestinationLabel = channel.DestinationLabel,
        Metadata = channel.Metadata,
        Secret = channel.KeepsStoredSecret
            ? retainedSecrets.GetValueOrDefault((channel.ChannelType, channel.Destination))
            : channel.Secret,
        SortOrder = sortOrder,
        CreatedAt = now,
    };

    private static readonly IReadOnlyDictionary<(ChannelType, string), string> NoRetainedSecrets =
        new Dictionary<(ChannelType, string), string>();

    /// <summary>
    /// The stored ciphertext of every channel that carries a signing secret, keyed by the pair a
    /// caller can still name after a read: a replacement mints new ids, and the secret is never
    /// echoed back, so a channel arriving without one is matched to its predecessor by type and
    /// destination.
    /// </summary>
    /// <remarks>
    /// A pair held by more than one stored secret retains none of them: the key cannot tell which
    /// of the duplicates an incoming channel descends from, and a guess would sign one receiver's
    /// alerts with another's secret. Ciphertext is compared rather than plaintext, so two channels
    /// sharing a destination are ambiguous even when the secret behind them is the same; they are
    /// re-entered rather than silently mismatched.
    /// </remarks>
    private static IReadOnlyDictionary<(ChannelType, string), string> RetainedSecrets(
        IEnumerable<AlertRuleChannelEntity> channels) => channels
            .Where(c => c.Secret is not null)
            .GroupBy(c => (c.ChannelType, c.Destination))
            .Select(g => (g.Key, Secrets: g.Select(c => c.Secret!).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Secrets.Length == 1)
            .ToDictionary(g => g.Key, g => g.Secrets[0]);
}
