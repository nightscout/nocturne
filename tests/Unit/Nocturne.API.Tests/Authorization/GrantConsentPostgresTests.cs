using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// A subject consenting to a client on PostgreSQL under the runtime role and Row Level Security:
/// overlapping first consents leave one active grant holding both scope sets, a grant revoked after
/// the consent read it is not written to, only an app grant stands in the consent's way, a removal of the
/// subject's membership that races a first consent leaves no active grant, an account deletion that
/// races a first consent deadlocks with neither, and a scopes edit that races a merge leaves the edit's
/// scopes whole.
/// </summary>
[Trait("Category", "Integration")]
public class GrantConsentPostgresTests(GrantConsentPostgresTests.Database database)
    : IClassFixture<GrantConsentPostgresTests.Database>
{
    // A statement that must stay blocked is watched for LockWait, or for DeadlockDetection where PostgreSQL
    // would otherwise break a deadlock; both are part of the assertion, not hang guards.
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeadlockDetection = TimeSpan.FromSeconds(3);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("grant_consent");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="before"/> once, just before the first statement <paramref name="when"/> accepts.</summary>
    private sealed class BeforeStatement(Func<DbCommand, bool> when, Func<Task> before) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        private async Task RunAsync(DbCommand command)
        {
            if (when(command) && Interlocked.Exchange(ref _fired, 1) == 0)
                await before();
        }
    }

    /// <summary>Records every statement that failed, so a deadlock a retry hid is still seen.</summary>
    private sealed class FailureLog : DbCommandInterceptor
    {
        private readonly List<Exception> _failures = [];

        public IReadOnlyList<Exception> Failures
        {
            get { lock (_failures) return [.. _failures]; }
        }

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            lock (_failures) _failures.Add(eventData.Exception);
            return Task.CompletedTask;
        }
    }

    private static bool WritesGrant(DbCommand command) =>
        command.CommandText.StartsWith("INSERT", StringComparison.Ordinal)
        && command.CommandText.Contains("oauth_grants", StringComparison.Ordinal);

    /// <summary>Accepts the <paramref name="nth"/> statement that names the grants table, counting from one.</summary>
    private static Func<DbCommand, bool> NthGrantStatement(int nth)
    {
        var seen = 0;
        return command => command.CommandText.Contains("oauth_grants", StringComparison.Ordinal)
            && Interlocked.Increment(ref seen) == nth;
    }

    private NocturneDbContext Context(Guid tenant, params IInterceptor[] staged) =>
        TestContexts.Context(database.AppConnectionString, tenant, staged);

    private sealed record Seeded(Guid Tenant, Guid Client, Guid Subject, SuppliedContextFactory Contexts);

    private async Task<Seeded> SeedAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'grant-consent-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"consent-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var subject = Guid.CreateVersion7();
        var client = Guid.CreateVersion7();
        await using (var db = Context(tenant))
        {
            db.Subjects.Add(new SubjectEntity { Id = subject, Name = "consenting subject" });
            db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject });
            db.OAuthClients.Add(new OAuthClientEntity { Id = client, ClientId = $"app-{client:N}", RedirectUris = "[]" });
            await db.SaveChangesAsync();
        }

        return new Seeded(tenant, client, subject, new SuppliedContextFactory(() => Context(tenant)));
    }

    private async Task<Guid> SeedGrantAsync(
        Seeded seeded, string grantType, List<string> scopes, DateTime? revokedAt = null)
    {
        var id = Guid.CreateVersion7();
        await using var db = Context(seeded.Tenant);
        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = id,
            ClientEntityId = seeded.Client,
            SubjectId = seeded.Subject,
            GrantType = grantType,
            Scopes = scopes,
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<StagedGrantConsent?> ConsentAsync(SuppliedContextFactory contexts, Seeded seeded, params string[] scopes)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(contexts);
        StagedGrantConsent? staged = null;
        await unit.CommitAsync(
            async token => staged = await new GrantConsentService(NullLogger<GrantConsentService>.Instance)
                .StageAsync(unit, seeded.Client, seeded.Subject, scopes, ct: token));
        return staged;
    }

    private static async Task RemoveMembershipAsync(SuppliedContextFactory contexts, Seeded seeded)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, seeded.Tenant);
        var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
        var member = (await members.FindMemberAsync(seeded.Tenant, seeded.Subject, CancellationToken.None))!;
        var revocation = new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())));
        await unit.CommitAsync(token => MembershipRemoval.StageAsync(
            unit, members, revocation, seeded.Tenant, member, MembershipEndHow.Removed, DateTime.UtcNow, token));
    }

    private async Task<List<OAuthGrantEntity>> GrantsAsync(Seeded seeded)
    {
        await using var db = Context(seeded.Tenant);
        return await db.OAuthGrants.AsNoTracking().Where(g => g.ClientEntityId == seeded.Client).ToListAsync();
    }

    [Fact]
    public async Task TwoFirstConsentsOfOneSubjectLeaveOneActiveGrantHoldingBothScopeSets()
    {
        var seeded = await SeedAsync();
        var firstIsAboutToCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeStatement(WritesGrant, () =>
            {
                firstIsAboutToCreate.SetResult();
                return releaseFirst.Task.WaitAsync(Eventually.DefaultPatience);
            })));

        var firstConsent = Task.Run(() => ConsentAsync(first, seeded, "glucose.read"));
        await firstIsAboutToCreate.Task.WaitAsync(Eventually.DefaultPatience);
        var secondConsent = Task.Run(() => ConsentAsync(seeded.Contexts, seeded, "treatments.read"));

        var finishedAhead = await Task.WhenAny(secondConsent, Task.Delay(LockWait));
        finishedAhead.Should().NotBe(secondConsent, "the second consent waits for the first to commit");
        releaseFirst.SetResult();
        var staged = await Task.WhenAll(firstConsent, secondConsent).WaitAsync(Eventually.DefaultPatience);

        var active = (await GrantsAsync(seeded)).Where(g => g.RevokedAt is null).ToList();
        active.Should().ContainSingle();
        active[0].Scopes.Should().BeEquivalentTo(["glucose.read", "treatments.read"]);
        staged.Count(s => s!.Created).Should().Be(1);
        staged.Select(s => s!.Grant.Id).Distinct().Should().ContainSingle().Which.Should().Be(active[0].Id);
    }

    [Fact]
    public async Task AGrantRevokedAfterTheConsentReadItIsNotWrittenToAndTheConsentCreatesANewOne()
    {
        var seeded = await SeedAsync();
        var revoked = await SeedGrantAsync(seeded, OAuthGrantTypes.App, ["glucose.read"]);
        var revokedAt = DateTime.UtcNow;
        var revoker = new OAuthGrantRepository(seeded.Contexts);
        var consenting = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeStatement(
                NthGrantStatement(2),
                () => revoker.RevokeAsync([revoked], revokedAt, CancellationToken.None))));

        var staged = await ConsentAsync(consenting, seeded, "treatments.read");

        staged!.Created.Should().BeTrue();
        staged.Grant.Id.Should().NotBe(revoked);
        staged.Grant.Scopes.Should().BeEquivalentTo(["treatments.read"]);
        var stored = await GrantsAsync(seeded);
        var old = stored.Single(g => g.Id == revoked);
        old.RevokedAt.Should().NotBeNull();
        old.Scopes.Should().BeEquivalentTo(["glucose.read"]);
        var active = stored.Where(g => g.RevokedAt is null).ToList();
        active.Should().ContainSingle().Which.Id.Should().Be(staged.Grant.Id);
    }

    [Fact]
    public async Task AScopesEditCommittedBetweenTheConsentsReadAndItsLockIsKeptByTheMerge()
    {
        var seeded = await SeedAsync();
        var grant = await SeedGrantAsync(seeded, OAuthGrantTypes.App, ["glucose.read", "entries.read"]);
        var consenting = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeStatement(NthGrantStatement(2), async () =>
            {
                await using var editor = Context(seeded.Tenant);
                var row = await editor.OAuthGrants.SingleAsync(g => g.Id == grant);
                row.Scopes = ["glucose.read"];
                await editor.SaveChangesAsync();
            })));

        var staged = await ConsentAsync(consenting, seeded, "treatments.read");

        staged!.Created.Should().BeFalse();
        staged.Grant.Scopes.Should().BeEquivalentTo(["glucose.read", "treatments.read"]);
        (await GrantsAsync(seeded)).Single(g => g.Id == grant).Scopes
            .Should().BeEquivalentTo(["glucose.read", "treatments.read"]);
    }

    [Fact]
    public async Task AConsentMergesIntoTheActiveGrantAndLeavesARevokedOneAlone()
    {
        var seeded = await SeedAsync();
        var revoked = await SeedGrantAsync(seeded, OAuthGrantTypes.App, ["entries.read"], DateTime.UtcNow.AddDays(-1));
        var active = await SeedGrantAsync(seeded, OAuthGrantTypes.App, ["glucose.read"]);

        var staged = await ConsentAsync(seeded.Contexts, seeded, "treatments.read");

        staged!.Created.Should().BeFalse();
        staged.Grant.Id.Should().Be(active);
        staged.Grant.Scopes.Should().BeEquivalentTo(["glucose.read", "treatments.read"]);
        (await GrantsAsync(seeded)).Single(g => g.Id == revoked).Scopes.Should().BeEquivalentTo(["entries.read"]);
    }

    [Fact]
    public async Task AGuestGrantNamingTheClientDoesNotStandInTheWayOfAConsent()
    {
        var seeded = await SeedAsync();
        var guest = await SeedGrantAsync(seeded, OAuthGrantTypes.Guest, ["glucose.read"]);

        var staged = await ConsentAsync(seeded.Contexts, seeded, "treatments.read");

        staged!.Created.Should().BeTrue();
        staged.Grant.Id.Should().NotBe(guest);
        staged.Grant.GrantType.Should().Be(OAuthGrantTypes.App);
        (await GrantsAsync(seeded)).Where(g => g.RevokedAt is null).Should().HaveCount(2);
    }

    [Fact]
    public async Task ARemovalThatRacesAFirstConsentLeavesTheRemovedMemberWithNoActiveGrant()
    {
        var seeded = await SeedAsync();
        var consentIsAboutToCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consenting = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeStatement(WritesGrant, () =>
            {
                consentIsAboutToCreate.SetResult();
                return releaseConsent.Task.WaitAsync(Eventually.DefaultPatience);
            })));

        var consent = Task.Run(() => ConsentAsync(consenting, seeded, "glucose.read"));
        await consentIsAboutToCreate.Task.WaitAsync(Eventually.DefaultPatience);
        var removal = Task.Run(() => RemoveMembershipAsync(seeded.Contexts, seeded));

        await Task.WhenAny(removal, Task.Delay(LockWait));
        releaseConsent.SetResult();
        await Task.WhenAll(consent, removal).WaitAsync(Eventually.DefaultPatience);

        (await GrantsAsync(seeded)).Where(g => g.RevokedAt is null).Should().BeEmpty();
        await using var db = Context(seeded.Tenant);
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == seeded.Subject)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAccountDeletionThatRacesAFirstConsentAndTheConsentBothFinishWithoutADeadlock()
    {
        var seeded = await SeedAsync();
        var failures = new FailureLog();
        var consentIsAboutToCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consenting = new SuppliedContextFactory(
            () => Context(seeded.Tenant, failures, new BeforeStatement(WritesGrant, () =>
            {
                consentIsAboutToCreate.SetResult();
                return releaseConsent.Task.WaitAsync(Eventually.DefaultPatience);
            })));
        var deleting = new SuppliedContextFactory(() => Context(seeded.Tenant, failures));

        var consent = Task.Run(() => ConsentAsync(consenting, seeded, "glucose.read"));
        await consentIsAboutToCreate.Task.WaitAsync(Eventually.DefaultPatience);
        var deletion = Task.Run(() =>
            new SubjectRepository(deleting, AccountLinkRoutes.Off).DeleteAsync(seeded.Subject, CancellationToken.None));

        var finishedAhead = await Task.WhenAny(deletion, Task.Delay(DeadlockDetection));
        finishedAhead.Should().NotBe(deletion, "the deletion waits for the consent to commit");
        releaseConsent.SetResult();
        await Task.WhenAll(consent, deletion).WaitAsync(Eventually.DefaultPatience);

        failures.Failures.Should().BeEmpty("neither side is aborted as a deadlock victim, and none is retried");
        (await consent)!.Created.Should().BeTrue();
        (await deletion).Should().BeOfType<SubjectChange.Changed>();
        await using var db = Context(seeded.Tenant);
        (await db.Subjects.AnyAsync(s => s.Id == seeded.Subject)).Should().BeFalse();
        (await GrantsAsync(seeded)).Should().BeEmpty("the grant the consent wrote goes with the subject");
    }

    [Fact]
    public async Task AConsentAfterTheRemovalFindsNoMembershipAndWritesNothing()
    {
        var seeded = await SeedAsync();
        await RemoveMembershipAsync(seeded.Contexts, seeded);

        var staged = await ConsentAsync(seeded.Contexts, seeded, "glucose.read");

        staged.Should().BeNull();
        (await GrantsAsync(seeded)).Should().BeEmpty();
    }

    [Fact]
    public async Task AScopesEditPausedBeforeItsSaveLeavesItsScopesWholeWhenAConsentMergesMeanwhile()
    {
        var seeded = await SeedAsync();
        var grant = await SeedGrantAsync(seeded, OAuthGrantTypes.App, ["glucose.read", "entries.read"]);
        StagedGrantConsent? merged = null;
        var editing = new SuppliedContextFactory(
            () => Context(seeded.Tenant, new BeforeStatement(
                command => command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                    && command.CommandText.Contains("oauth_grants", StringComparison.Ordinal),
                async () => merged = await ConsentAsync(seeded.Contexts, seeded, "treatments.read"))));
        var guestSessions = new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()));
        var service = new OAuthGrantService(
            editing,
            new OAuthGrantRepository(editing),
            Mock.Of<IOAuthClientService>(),
            guestSessions,
            new GrantRevocationService(guestSessions),
            new GrantConsentService(NullLogger<GrantConsentService>.Instance),
            TimeProvider.System,
            NullLogger<OAuthGrantService>.Instance);

        var edited = await service.UpdateGrantAsync(grant, seeded.Subject, new GrantScopeBounds(new HashSet<string> { Scope.FullAccess }, new HashSet<string> { Scope.FullAccess }), scopes: ["glucose.read"]);

        merged.Should().NotBeNull("the consent merged while the edit was paused before its save");
        merged!.Created.Should().BeFalse();
        merged.Grant.Id.Should().Be(grant);
        merged.Grant.Scopes.Should().Contain("treatments.read");
        edited!.Scopes.Should().BeEquivalentTo(["glucose.read"]);
        (await GrantsAsync(seeded)).Single(g => g.Id == grant).Scopes.Should().BeEquivalentTo(["glucose.read"]);
    }
}
