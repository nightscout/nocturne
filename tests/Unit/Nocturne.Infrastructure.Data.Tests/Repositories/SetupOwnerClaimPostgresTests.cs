using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The first credential store claims the setup, on PostgreSQL under the runtime role: a store that
/// starts while another holds the tenant's setup lock waits for it, then stores nothing if the
/// other committed its claim and stores its credential if the other was abandoned.
/// </summary>
[Trait("Category", "Integration")]
public class SetupOwnerClaimPostgresTests(SetupOwnerClaimPostgresTests.Database database)
    : IClassFixture<SetupOwnerClaimPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("setup_owner_claim");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static NocturneDbContext Context(string connectionString, Guid tenant, bool retrying = false) =>
        TestContexts.Context(connectionString, tenant, npgsql: retrying ? TestContexts.Retrying : null);

    /// <summary>Contexts as the setup request's scope opens them: no tenant, with the runtime retry strategy.</summary>
    private SuppliedContextFactory Contexts(bool retrying) =>
        new(() => Context(database.AppConnectionString, Guid.Empty, retrying));

    private static string Issuer(Guid provider) => $"https://{provider:N}.issuer.example";

    private sealed record Site(Guid Tenant, string Slug, Guid Owner, Guid Provider, SuppliedContextFactory Seeding, SuppliedContextFactory Runtime);

    private async Task<Site> SiteAsync(string name)
    {
        var tenant = Guid.NewGuid();
        var slug = $"{name}-{tenant:N}";
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}', 'setup-claim', true, now(), now());
            """);
        var seeding = Contexts(retrying: false);
        var owner = await new SubjectRepository(seeding, AccountLinkRoutes.Off)
            .CreateSetupOwnerAsync(tenant, new NewSubject("Owner", Username: $"owner-{tenant:N}"), Ct);
        var provider = Guid.NewGuid();
        await using (var db = Context(database.AppConnectionString, Guid.Empty))
        {
            db.OidcProviders.Add(new OidcProviderEntity
            {
                Id = provider, Name = "Keycloak", IssuerUrl = Issuer(provider), ClientId = $"c-{provider:N}", IsEnabled = true,
            });
            await db.SaveChangesAsync();
        }
        return new Site(tenant, slug, owner, provider, seeding, Contexts(retrying: true));
    }

    private static Task<Guid?> StoreAsync(Site site, string store) => store switch
    {
        "passkey" => new PasskeyCredentialRepository(site.Runtime).CreateForSetupOwnerAsync(
            site.Tenant, new NewPasskeyCredential(site.Owner, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, DateTime.UtcNow), Ct),
        "provider identity" => new SubjectRepository(site.Runtime, AccountLinkRoutes.Off).AddSetupOwnerOidcIdentityAsync(
            site.Tenant, new NewSubjectOidcIdentity(site.Owner, site.Provider, $"ext-{Guid.NewGuid():N}", Issuer(site.Provider), null, DateTime.UtcNow), Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, null),
    };

    /// <summary>Another completion's store, holding the setup lock with its claim made but not committed.</summary>
    private async Task<(NocturneDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx)> ClaimHeldAsync(Site site, Guid subject)
    {
        var db = Context(database.AppConnectionString, Guid.Empty);
        var tx = await db.Database.BeginTransactionAsync();
        (await db.ClaimSetupAsync(site.Tenant, subject, Ct)).Should().BeTrue();
        await db.SaveChangesAsync();
        return (db, tx);
    }

    [Theory]
    [InlineData("passkey")]
    [InlineData("provider identity")]
    public async Task A_store_waits_on_a_claim_under_way_and_stores_nothing_once_it_commits(string store)
    {
        var site = await SiteAsync("lost");
        var (holding, held) = await ClaimHeldAsync(site, site.Owner);
        await using var _ = holding;

        var stored = StoreAsync(site, store);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, stored);
        await held.CommitAsync();

        (await stored.WaitAsync(Eventually.DefaultPatience)).Should().BeNull();
        (await CredentialsAsync(site.Owner)).Should().Be(0);
        (await RecordAsync(site.Tenant)).Should().BeNull();
    }

    [Theory]
    [InlineData("passkey")]
    [InlineData("provider identity")]
    public async Task A_claim_that_is_abandoned_leaves_the_setup_to_the_store_that_waited(string store)
    {
        var site = await SiteAsync("abandoned");
        var (holding, held) = await ClaimHeldAsync(site, site.Owner);
        await using var _ = holding;

        var stored = StoreAsync(site, store);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, stored);
        await held.RollbackAsync();

        (await stored.WaitAsync(Eventually.DefaultPatience)).Should().NotBeNull();
        (await CredentialsAsync(site.Owner)).Should().Be(1);
        (await RecordAsync(site.Tenant)).Should().BeNull();
    }

    [Fact]
    public async Task An_adoption_that_read_before_a_claim_committed_is_retried_and_refused()
    {
        var site = await SiteAsync("adopt");
        var members = new TenantMembershipRepository(site.Seeding, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(site.Tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Owner, Ct))!.Value;
        (await members.AddMemberAsync(
            new NewTenantMember(site.Tenant, site.Owner, [ownerRole]) { How = MembershipJoinHow.Setup, By = null }, Ct))
            .Should().BeTrue();
        var seedingSubjects = new SubjectRepository(site.Seeding, AccountLinkRoutes.Off);
        var holder = await seedingSubjects.CreateAsync(new NewSubject("Holder"), Ct);
        var identity = await seedingSubjects.AddOidcIdentityAsync(
            new NewSubjectOidcIdentity(holder, site.Provider, $"ext-{Guid.NewGuid():N}", Issuer(site.Provider), null, DateTime.UtcNow), Ct);
        var (holding, held) = await ClaimHeldAsync(site, site.Owner);
        await using var _ = holding;

        var adoption = new SubjectRepository(site.Runtime, AccountLinkRoutes.Off)
            .AdoptForSetupAsync(site.Owner, holder, identity, site.Slug, Ct);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, adoption);
        await held.CommitAsync();

        (await adoption.WaitAsync(Eventually.DefaultPatience)).Should().BeFalse();
        (await members.FindMemberAsync(site.Tenant, holder, Ct)).Should().BeNull();
        (await members.FindMemberAsync(site.Tenant, site.Owner, Ct)).Should().NotBeNull();
        (await seedingSubjects.GetAsync(site.Owner, Ct)).Should().NotBeNull("a refused adoption keeps the owner-step subject");
    }

    /// <remarks>
    /// The holder manages a team of their own, so a deletion holds the holder's row <c>FOR UPDATE</c>
    /// while it waits at the team's manager lock. An adoption ahead holds the holder's row and waits at
    /// the holder's membership lock; the deletion then waits at the row and is refused once the holder
    /// owns the site. A deletion ahead deletes the holder while the adoption waits at the row, and the
    /// adoption's retry finds no holder to adopt.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_adoption_and_the_holders_account_deletion_at_once_either_adopt_or_refuse_cleanly(bool adoptFirst)
    {
        var site = await SiteAsync($"adopt-delete-{adoptFirst}");
        var members = new TenantMembershipRepository(site.Seeding, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(site.Tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Owner, Ct))!.Value;
        (await members.AddMemberAsync(
            new NewTenantMember(site.Tenant, site.Owner, [ownerRole]) { How = MembershipJoinHow.Setup, By = null }, Ct))
            .Should().BeTrue();
        var seedingSubjects = new SubjectRepository(site.Seeding, AccountLinkRoutes.Off);
        var holder = await seedingSubjects.CreateAsync(new NewSubject("Holder"), Ct);
        var identity = await seedingSubjects.AddOidcIdentityAsync(
            new NewSubjectOidcIdentity(holder, site.Provider, $"ext-{Guid.NewGuid():N}", Issuer(site.Provider), null, DateTime.UtcNow), Ct);
        var team = (await new TeamRepository(site.Seeding).CreateAsync(holder, "Clinic", Ct)).Id;

        await using var holding = Context(database.AppConnectionString, Guid.Empty);
        await using var held = await holding.Database.BeginTransactionAsync();
        Task<bool> adoption;
        Task<SubjectChange> deletion;
        if (adoptFirst)
        {
            await holding.LockMembershipAsync(site.Tenant, holder, Ct);
            adoption = new SubjectRepository(site.Runtime, AccountLinkRoutes.Off).AdoptForSetupAsync(site.Owner, holder, identity, site.Slug, Ct);
            await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, adoption);
            deletion = new SubjectRepository(site.Runtime, AccountLinkRoutes.Off).DeleteAsync(holder, Ct);
        }
        else
        {
            await holding.LockTeamManagersAsync(team, Ct);
            deletion = new SubjectRepository(site.Runtime, AccountLinkRoutes.Off).DeleteAsync(holder, Ct);
            await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, deletion);
            adoption = new SubjectRepository(site.Runtime, AccountLinkRoutes.Off).AdoptForSetupAsync(site.Owner, holder, identity, site.Slug, Ct);
        }

        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 2, adoption, deletion);
        await held.RollbackAsync();

        (await adoption.WaitAsync(Eventually.DefaultPatience)).Should().Be(adoptFirst);
        var deleted = await deletion.WaitAsync(Eventually.DefaultPatience);
        if (adoptFirst)
        {
            deleted.Should().BeOfType<SubjectChange.LastOwnerOf>();
            (await members.FindMemberAsync(site.Tenant, holder, Ct)).Should().NotBeNull();
        }
        else
        {
            deleted.Should().BeOfType<SubjectChange.Changed>();
            (await seedingSubjects.GetAsync(holder, Ct)).Should().BeNull();
            (await members.FindMemberAsync(site.Tenant, site.Owner, Ct)).Should().NotBeNull("a refused adoption keeps the owner-step subject");
        }
    }

    [Fact]
    public async Task The_claim_takes_the_setup_lock_of_its_tenant()
    {
        var site = await SiteAsync("lock");
        var (holding, held) = await ClaimHeldAsync(site, site.Owner);
        await using var _ = holding;

        (await holding.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks
             WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND objsubid = 2
               AND classid = {(long)SetupOwnerClaim.Class}
            """).SingleAsync()).Should().Be(1);
        await held.RollbackAsync();
    }

    private async Task<long> CredentialsAsync(Guid subject)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM passkey_credentials WHERE subject_id = @subject)
                 + (SELECT count(*) FROM subject_oidc_identities WHERE subject_id = @subject);
            """, conn);
        cmd.Parameters.AddWithValue("subject", subject);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid?> RecordAsync(Guid tenant)
    {
        await using var db = Context(database.AppConnectionString, Guid.Empty);
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenant).Select(t => t.SetupOwnerSubjectId).SingleAsync();
    }
}
