using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
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

        var subjects = new SubjectRepository(new Factory(Context, tenant));
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
        await using (var db = Context(b.Tenant))
        {
            // Tenant b's orphan gets a passkey, so b has credentials and no orphan.
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = b.Orphan, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1],
            });
            await db.SaveChangesAsync();
        }
        var readingAsB = new SubjectRepository(new Factory(Context, b.Tenant));
        var readingAsNoOne = new SubjectRepository(new Factory(Context, Guid.Empty));

        (await readingAsB.TenantHasCredentialedMemberAsync(a.Tenant, CancellationToken.None)).Should().BeTrue();
        (await readingAsB.HasOrphanedMemberAsync(a.Tenant, CancellationToken.None)).Should().BeTrue();
        (await readingAsNoOne.TenantHasCredentialedMemberAsync(a.Tenant, CancellationToken.None)).Should().BeTrue();
        (await readingAsNoOne.HasOrphanedMemberAsync(a.Tenant, CancellationToken.None)).Should().BeTrue();
        (await readingAsNoOne.HasOrphanedMemberAsync(b.Tenant, CancellationToken.None)).Should().BeFalse();
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
        var subjects = new SubjectRepository(factory);

        // Read as the scope, the credentialed subject's row is hidden by subject_pin; the repository
        // reaches it only by pinning the read to that subject.
        (await new SubjectRepository(new Factory(Context, Guid.Empty)).IsPlatformAdminAsync(a.Credentialed, CancellationToken.None))
            .Should().BeTrue("unpinned, the flag is there to read");
        (await subjects.IsPlatformAdminAsync(a.Credentialed, CancellationToken.None)).Should().BeFalse("the scope's reader is another subject");
        (await subjects.GetSignInStateAsync(a.Credentialed, CancellationToken.None)).Should().Be(new SubjectSignInState(true, true));
        (await subjects.GetSignInStateAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
        factory.PinnedTo.Should().HaveCount(2).And.StartWith(a.Credentialed);
    }
}
