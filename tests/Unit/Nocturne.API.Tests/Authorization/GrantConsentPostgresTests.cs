using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
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
/// the consent read it is not written to, and only an app grant stands in the consent's way.
/// </summary>
[Trait("Category", "Integration")]
public class GrantConsentPostgresTests(GrantConsentPostgresTests.Database database)
    : IClassFixture<GrantConsentPostgresTests.Database>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(1);

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

    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(create(scope));
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

    private NocturneDbContext Context(Guid tenant, IInterceptor? staged = null) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(staged is null
                ? [new TenantConnectionInterceptor()]
                : [new TenantConnectionInterceptor(), staged])
            .Options) { TenantId = tenant };

    private sealed record Seeded(Guid Tenant, Guid Client, Guid Subject, Factory Contexts);

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
            db.OAuthClients.Add(new OAuthClientEntity { Id = client, ClientId = $"app-{client:N}", RedirectUris = "[]" });
            await db.SaveChangesAsync();
        }

        return new Seeded(tenant, client, subject, new Factory(t => Context(t), tenant));
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

    private static async Task<StagedGrantConsent> ConsentAsync(Factory contexts, Seeded seeded, params string[] scopes)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(contexts);
        StagedGrantConsent? staged = null;
        await unit.CommitAsync(
            async token => staged = await new GrantConsentService(NullLogger<GrantConsentService>.Instance)
                .StageAsync(unit, seeded.Client, seeded.Subject, scopes, ct: token));
        return staged!;
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
        var first = new Factory(
            tenant => Context(tenant, new BeforeStatement(WritesGrant, () =>
            {
                firstIsAboutToCreate.SetResult();
                return releaseFirst.Task.WaitAsync(Patience);
            })),
            seeded.Tenant);

        var firstConsent = Task.Run(() => ConsentAsync(first, seeded, "glucose.read"));
        await firstIsAboutToCreate.Task.WaitAsync(Patience);
        var secondConsent = Task.Run(() => ConsentAsync(seeded.Contexts, seeded, "treatments.read"));

        var finishedAhead = await Task.WhenAny(secondConsent, Task.Delay(LockWait));
        finishedAhead.Should().NotBe(secondConsent, "the second consent waits for the first to commit");
        releaseFirst.SetResult();
        var staged = await Task.WhenAll(firstConsent, secondConsent).WaitAsync(Patience);

        var active = (await GrantsAsync(seeded)).Where(g => g.RevokedAt is null).ToList();
        active.Should().ContainSingle();
        active[0].Scopes.Should().BeEquivalentTo(["glucose.read", "treatments.read"]);
        staged.Count(s => s.Created).Should().Be(1);
        staged.Select(s => s.Grant.Id).Distinct().Should().ContainSingle().Which.Should().Be(active[0].Id);
    }

    [Fact]
    public async Task AGrantRevokedAfterTheConsentReadItIsNotWrittenToAndTheConsentCreatesANewOne()
    {
        var seeded = await SeedAsync();
        var revoked = await SeedGrantAsync(seeded, OAuthGrantTypes.App, ["glucose.read"]);
        var revokedAt = DateTime.UtcNow;
        var revoker = new OAuthGrantRepository(seeded.Contexts);
        var consenting = new Factory(
            tenant => Context(tenant, new BeforeStatement(
                NthGrantStatement(2),
                () => revoker.RevokeAsync([revoked], revokedAt, CancellationToken.None))),
            seeded.Tenant);

        var staged = await ConsentAsync(consenting, seeded, "treatments.read");

        staged.Created.Should().BeTrue();
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
        var consenting = new Factory(
            tenant => Context(tenant, new BeforeStatement(NthGrantStatement(2), async () =>
            {
                await using var editor = Context(seeded.Tenant);
                var row = await editor.OAuthGrants.SingleAsync(g => g.Id == grant);
                row.Scopes = ["glucose.read"];
                await editor.SaveChangesAsync();
            })),
            seeded.Tenant);

        var staged = await ConsentAsync(consenting, seeded, "treatments.read");

        staged.Created.Should().BeFalse();
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

        staged.Created.Should().BeFalse();
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

        staged.Created.Should().BeTrue();
        staged.Grant.Id.Should().NotBe(guest);
        staged.Grant.GrantType.Should().Be(OAuthGrantTypes.App);
        (await GrantsAsync(seeded)).Where(g => g.RevokedAt is null).Should().HaveCount(2);
    }
}
