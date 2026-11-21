using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The pinned reads of <see cref="SubjectRepository"/> on PostgreSQL under the runtime role and Row
/// Level Security: the system-pinned setup-state reads answer for the tenant they pin from a scope
/// that reads as another tenant or as no one, and the subject-pinned sign-in state read runs under
/// the <c>subject_pin</c> policy, which admits that subject's row and hides every other.
/// </summary>
[Trait("Category", "Integration")]
public class SubjectRepositoryPostgresTests(SubjectRepositoryPostgresTests.Database database)
    : IClassFixture<SubjectRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("subject_repo");
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

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private sealed record Seeded(Guid Tenant, Guid Credentialed, Guid Orphan);

    /// <summary>A tenant with one member holding a passkey and one holding nothing.</summary>
    private async Task<Seeded> SeedTenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'subject-repo-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var subjects = new SubjectRepository(new Factory(Context, tenant), AccountLinkRoutes.Off);
        var credentialed = await subjects.CreateAsync(new NewSubject($"{slug}-credentialed"), CancellationToken.None);
        var orphan = await subjects.CreateAsync(new NewSubject($"{slug}-orphan"), CancellationToken.None);
        // A flag that reads true when the row is visible, so a hidden row is told from a false one.
        await subjects.GrantPlatformAdminAsync([credentialed], CancellationToken.None);
        await new PasskeyCredentialRepository(new Factory(Context, tenant)).CreateAsync(
            new NewPasskeyCredential(credentialed, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, DateTime.UtcNow),
            CancellationToken.None);
        await using (var db = Context(tenant))
        {
            db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = credentialed });
            db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = orphan });
            await db.SaveChangesAsync();
        }

        return new Seeded(tenant, credentialed, orphan);
    }

    [Fact]
    public async Task SystemPinned_TheSetupStateReadsAnswerForThePinnedTenantFromAnyScope()
    {
        var a = await SeedTenantAsync("a");
        var b = await SeedTenantAsync("b");
        var readingAsB = new SubjectRepository(new Factory(Context, b.Tenant), AccountLinkRoutes.Off);
        var readingAsNoOne = new SubjectRepository(new Factory(Context, Guid.Empty), AccountLinkRoutes.Off);

        (await readingAsB.TenantHasCredentialedMemberAsync(a.Tenant, CancellationToken.None)).Should().BeTrue();
        (await readingAsNoOne.TenantHasCredentialedMemberAsync(a.Tenant, CancellationToken.None)).Should().BeTrue();
        (await readingAsNoOne.TenantHasCredentialedMemberAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

    /// <summary>
    /// A scope whose every unpinned entry point reads as <paramref name="scopeSubject"/>, so under
    /// the <c>subject_pin</c> policy any other subject's row is hidden from it; only
    /// <see cref="CreateForSubjectAsync"/> reaches another subject, and records that it was taken.
    /// </summary>
    private sealed class SubjectScopedFactory(Func<Guid, NocturneDbContext> create, Guid scopeSubject) : INocturneDbContextFactory
    {
        public List<Guid> PinnedTo { get; } = [];

        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => AsScope();

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) => AsScope();

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => AsScope();

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) => AsScope();

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
        {
            PinnedTo.Add(subjectId);
            var context = create(Guid.Empty);
            context.SubjectId = subjectId;
            return ValueTask.FromResult(context);
        }

        private ValueTask<NocturneDbContext> AsScope()
        {
            var context = create(Guid.Empty);
            context.SubjectId = scopeSubject;
            return ValueTask.FromResult(context);
        }
    }

    [Fact]
    public async Task SubjectPinned_TheSignInStateIsReadAsTheSubjectItselfAndNotAsTheScope()
    {
        var a = await SeedTenantAsync("c");
        var factory = new SubjectScopedFactory(Context, scopeSubject: a.Orphan);
        var subjects = new SubjectRepository(factory, AccountLinkRoutes.Off);

        // Read as the scope, the credentialed subject's row is hidden by subject_pin; the repository
        // reaches it only by pinning the read to that subject.
        (await new SubjectRepository(new Factory(Context, Guid.Empty), AccountLinkRoutes.Off).IsPlatformAdminAsync(a.Credentialed, CancellationToken.None))
            .Should().BeTrue("unpinned, the flag is there to read");
        (await subjects.IsPlatformAdminAsync(a.Credentialed, CancellationToken.None)).Should().BeFalse("the scope's reader is another subject");
        (await subjects.GetSignInStateAsync(a.Credentialed, CancellationToken.None)).Should().Be(new SubjectSignInState(true, true));
        (await subjects.GetSignInStateAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
        factory.PinnedTo.Should().HaveCount(2).And.StartWith(a.Credentialed);
    }

    [Fact]
    public async Task TheLastActivePlatformAdminIsKeptUnderThePlatformAdminLock()
    {
        await ClearPlatformAdminsAsync();

        var subjects = new SubjectRepository(new Factory(Context, Guid.Empty), AccountLinkRoutes.Off);
        var demoted = await subjects.CreateAsync(new NewSubject("lock-demoted"), CancellationToken.None);
        var last = await subjects.CreateAsync(new NewSubject("lock-last"), CancellationToken.None);
        await subjects.GrantPlatformAdminAsync([demoted, last], CancellationToken.None);

        (await subjects.UpdateAsync(demoted, new SubjectEdit(IsPlatformAdmin: false), CancellationToken.None))
            .Should().BeOfType<SubjectChange.Changed>();
        (await subjects.UpdateAsync(last, new SubjectEdit(IsActive: false), CancellationToken.None))
            .Should().BeOfType<SubjectChange.LastActivePlatformAdmin>();
        (await subjects.DeleteAsync(last, CancellationToken.None))
            .Should().BeOfType<SubjectChange.LastActivePlatformAdmin>();
        (await subjects.IsPlatformAdminAsync(last, CancellationToken.None)).Should().BeTrue();
    }

    /// <summary>
    /// Two demotions of the instance's last two active admins, both started while another
    /// transaction holds the platform-admin lock, so each reaches its read only once the lock is
    /// free. Without the lock both read two admins and both commit.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentDemotionsOfTheLastTwoActiveAdminsLeaveOne()
    {
        await ClearPlatformAdminsAsync();
        var subjects = new SubjectRepository(new Factory(Context, Guid.Empty), AccountLinkRoutes.Off);
        var a = await subjects.CreateAsync(new NewSubject("race-a"), CancellationToken.None);
        var b = await subjects.CreateAsync(new NewSubject("race-b"), CancellationToken.None);
        await subjects.GrantPlatformAdminAsync([a, b], CancellationToken.None);

        await using var holder = new NpgsqlConnection(database.AppConnectionString);
        await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@class, 0)", holder, held))
        {
            lockCmd.Parameters.AddWithValue("class", PlatformAdminFilter.LockClass);
            await lockCmd.ExecuteNonQueryAsync();
        }

        var demoteA = Task.Run(() => subjects.UpdateAsync(a, new SubjectEdit(IsPlatformAdmin: false), CancellationToken.None));
        var demoteB = Task.Run(() => subjects.UpdateAsync(b, new SubjectEdit(IsPlatformAdmin: false), CancellationToken.None));
        var both = Task.WhenAll(demoteA, demoteB);

        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(1)))).Should().NotBeSameAs(both,
            "each demotion waits on the platform-admin lock before it reads the admins");

        await held.CommitAsync();
        var results = await both.WaitAsync(TimeSpan.FromSeconds(30));

        results.Should().ContainSingle(r => r is SubjectChange.Changed)
            .And.ContainSingle(r => r is SubjectChange.LastActivePlatformAdmin);
        var remaining = new[]
        {
            await subjects.IsPlatformAdminAsync(a, CancellationToken.None),
            await subjects.IsPlatformAdminAsync(b, CancellationToken.None),
        };
        remaining.Should().ContainSingle(isAdmin => isAdmin);
    }

    /// <summary>Clears every subject's flag, so this class's earlier admins do not count.</summary>
    private async Task ClearPlatformAdminsAsync()
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE subjects SET is_platform_admin = false";
        await cmd.ExecuteNonQueryAsync();
    }
}
