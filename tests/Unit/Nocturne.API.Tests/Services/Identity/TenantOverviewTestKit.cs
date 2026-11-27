using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The membership store and the per-tenant scope plumbing the cross-tenant overview and review
/// tests share.
/// </summary>
internal static class TenantOverviewTestKit
{
    /// <summary>A caller in no group as staff.</summary>
    public static Mock<ITeamRepository> InNoGroup()
    {
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetFirstGroupOfPeopleAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, Guid>());
        return teams;
    }

    public static DbContextOptions<NocturneDbContext> NewOptions() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    public static Guid SeedMembership(
        DbContextOptions<NocturneDbContext> options,
        Guid subjectId,
        string slug,
        List<string>? rolePermissions,
        List<string>? directPermissions = null,
        bool tenantActive = true,
        bool limitTo24Hours = false,
        DateTime? lastReadingAt = null,
        DateTime? accessEndsAt = null)
    {
        using var db = new NocturneDbContext(options);
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            IsActive = tenantActive,
            LastReadingAt = lastReadingAt,
        };
        db.Tenants.Add(tenant);

        if (!db.Subjects.Any(s => s.Id == subjectId))
            db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Member" });

        var member = new TenantMemberEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            SubjectId = subjectId,
            DirectPermissions = directPermissions,
            LimitTo24Hours = limitTo24Hours,
            AccessEndsAt = accessEndsAt,
        };
        db.TenantMembers.Add(member);

        if (rolePermissions is not null)
        {
            var role = new TenantRoleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = "role",
                Slug = $"role-{slug}",
                Permissions = rolePermissions,
            };
            db.TenantRoles.Add(role);
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.NewGuid(),
                TenantMemberId = member.Id,
                TenantRoleId = role.Id,
            });
        }

        db.SaveChanges();
        return tenant.Id;
    }

    /// <summary>
    /// The per-tenant scope's context factory, pinned to that scope's tenant and recording the
    /// scope's clamp when a context is taken.
    /// </summary>
    public sealed class ScopeContextFactory(
        DbContextOptions<NocturneDbContext> options,
        Func<Guid> tenantId,
        ICategoryReadContext? category = null,
        Dictionary<Guid, bool>? clampSeen = null) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
        {
            var context = new NocturneDbContext(options) { TenantId = tenantId() };
            if (clampSeen is not null)
                clampSeen[context.TenantId] = category?.IsHistoryClamped == true;
            return ValueTask.FromResult(context);
        }
    }

    public sealed class InMemoryContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    /// <summary>A reason a rule of the caller's gives, at <paramref name="level"/>.</summary>
    public static TenantOverviewAttention RuleReason(NotificationUrgency level) =>
        new(TenantOverviewAttentionKind.FlagRule, level)
        {
            Rule = new TenantOverviewRuleReason(
                Guid.NewGuid(), FlagRuleSource.Yours, "glucose_statistics", "timeBelow", "Time below range", "%", "consensus",
                6.5, FlagRuleComparison.Above, 4, 14, DateTime.UtcNow.AddDays(-14), DateTime.UtcNow, null, null),
        };

    /// <summary>A run that gives each person the reasons scripted for them, and records what the overview passed it.</summary>
    public sealed class ScriptedRules : IPersonalFlagRules
    {
        public Dictionary<Guid, IReadOnlyList<TenantOverviewAttention>> Reasons { get; } = [];
        public int Pending { get; init; }
        public bool None { get; init; }
        public int Begun { get; private set; }
        public List<Guid> Subjects { get; } = [];
        public List<bool> CredentialLimits { get; } = [];
        public List<(GlucoseReadTenant Person, SensorGlucose? Latest, IReadOnlyList<TrackerInstance>? Trackers, PumpSnapshot? Pump)> Judged { get; } = [];
        public int? FinishedAfter { get; private set; }

        public Task<IPersonalFlagRuleRun?> BeginAsync(Guid subjectId, bool credentialLimitTo24Hours, CancellationToken ct)
        {
            Begun++;
            Subjects.Add(subjectId);
            CredentialLimits.Add(credentialLimitTo24Hours);
            return Task.FromResult<IPersonalFlagRuleRun?>(None ? null : new Run(this));
        }

        private sealed class Run(ScriptedRules owner) : IPersonalFlagRuleRun
        {
            public int PeopleWithRulesPending => owner.Pending;

            public Task<IReadOnlyList<TenantOverviewAttention>> ReasonsAsync(
                GlucoseReadTenant person, IServiceProvider personScope, SensorGlucose? latest,
                IReadOnlyList<TrackerInstance>? activeTrackers, PumpSnapshot? pump, CancellationToken ct)
            {
                lock (owner.Judged)
                    owner.Judged.Add((person, latest, activeTrackers, pump));
                return Task.FromResult(owner.Reasons.GetValueOrDefault(person.Tenant.Id) ?? []);
            }

            public void Finish() => owner.FinishedAfter = owner.Judged.Count;
        }
    }

    /// <summary>The errors the overview logs, each a read of one person that failed and was stood in for.</summary>
    public sealed class ErrorLog : ILogger<TenantOverviewService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Messages.Add($"{formatter(state, exception)}: {exception}");
        }
    }
}
