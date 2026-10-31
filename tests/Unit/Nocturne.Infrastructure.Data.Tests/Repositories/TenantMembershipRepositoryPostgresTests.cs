using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The pins of <see cref="TenantMembershipRepository"/> on PostgreSQL under the runtime role and
/// Row Level Security: the system-pinned setup-gate read answers for the tenant it pins from a
/// scope that reads as another tenant or as no one; a subject-pinned enumeration runs under the
/// <c>subject_pin</c> policy, which admits that subject's rows and hides every other; and a
/// tenant-pinned request read reaches a tenant-scoped table the scope's own tenant could not.
/// </summary>
[Trait("Category", "Integration")]
public class TenantMembershipRepositoryPostgresTests(TenantMembershipRepositoryPostgresTests.Database database)
    : IClassFixture<TenantMembershipRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("tenant_membership_repo");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// A scope whose every entry point reads as <paramref name="scope"/>, except the system pin,
    /// which reads as the tenant it names and records that it was taken.
    /// </summary>
    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : INocturneDbContextFactory
    {
        public List<Guid> SystemPinnedTo { get; } = [];

        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create(scope));

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
        {
            var context = create(Guid.Empty);
            context.SubjectId = subjectId;
            return ValueTask.FromResult(context);
        }

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default)
        {
            SystemPinnedTo.Add(tenantId);
            return ValueTask.FromResult(create(tenantId));
        }
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private sealed record Seeded(Guid Tenant, Guid Owner, Guid Orphan);

    /// <summary>A tenant with its seed roles, an owner holding a passkey and a caretaker holding nothing.</summary>
    private async Task<Seeded> SeedTenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'membership-repo-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var factory = new Factory(Context, tenant);
        var subjects = new SubjectRepository(factory);
        var members = new TenantMembershipRepository(factory);
        var owner = await subjects.CreateAsync(new NewSubject($"{slug}-owner"), CancellationToken.None);
        var orphan = await subjects.CreateAsync(new NewSubject($"{slug}-orphan"), CancellationToken.None);
        await new PasskeyCredentialRepository(factory).CreateAsync(
            new NewPasskeyCredential(owner, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, DateTime.UtcNow),
            CancellationToken.None);
        await members.SeedRolesAsync(tenant, CancellationToken.None);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]), CancellationToken.None)).Should().BeTrue();
        (await members.AddMemberAsync(new NewTenantMember(tenant, orphan, [caretakerRole]), CancellationToken.None)).Should().BeTrue();

        return new Seeded(tenant, owner, orphan);
    }

    [Fact]
    public async Task SystemPinned_TheSetupGateAndTheOwnersAnswerForThePinnedTenantFromAnyScope()
    {
        var a = await SeedTenantAsync("a");
        var b = await SeedTenantAsync("b");
        var readingAsB = new Factory(Context, b.Tenant);
        var readingAsNoOne = new Factory(Context, Guid.Empty);
        var fromB = new TenantMembershipRepository(readingAsB);
        var fromNoOne = new TenantMembershipRepository(readingAsNoOne);
        var unknown = Guid.NewGuid();

        (await fromB.ReadSetupGateAsync(a.Tenant, CancellationToken.None)).Should().Be(new SetupGate(true, true));
        (await fromB.GetOwnerSubjectIdsForAccessRequestAsync(a.Tenant, CancellationToken.None)).Should().Equal(a.Owner);
        (await fromNoOne.ReadSetupGateAsync(a.Tenant, CancellationToken.None)).Should().Be(new SetupGate(true, true));
        (await fromNoOne.CountSetupCredentialsAsync(a.Tenant, CancellationToken.None)).Should().Be(new SetupCredentialCounts(2, 1, 0));
        (await fromNoOne.ListOrphanedMembersAsync(a.Tenant, CancellationToken.None)).Select(o => o.SubjectId).Should().Equal(a.Orphan);
        (await fromNoOne.ReadSetupGateAsync(unknown, CancellationToken.None)).Should().BeNull();

        readingAsB.SystemPinnedTo.Should().Equal(new[] { a.Tenant, a.Tenant }, "the gate and the owner read each took the system pin");
        readingAsNoOne.SystemPinnedTo.Should().Equal(a.Tenant, a.Tenant, a.Tenant, unknown);
    }

    /// <summary>
    /// A scope whose every unpinned entry point reads as <paramref name="scopeSubject"/>, so under
    /// the <c>subject_pin</c> policy any other subject's membership rows are hidden from it; only
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
    public async Task SubjectPinned_AnotherSubjectsMembershipsAreReadAsThatSubjectAndNotAsTheScope()
    {
        var a = await SeedTenantAsync("c");
        var factory = new SubjectScopedFactory(Context, scopeSubject: a.Orphan);
        var members = new TenantMembershipRepository(factory);

        // Read as the scope, the owner's membership row is hidden by subject_pin; the repository
        // reaches it only by pinning the read to that subject.
        (await members.IsMemberAsync(a.Tenant, a.Owner, CancellationToken.None)).Should().BeFalse("the scope's reader is another subject");
        (await members.IsMemberAsync(a.Tenant, a.Orphan, CancellationToken.None)).Should().BeTrue();
        (await members.ListTenantIdsOfSubjectAsync(a.Owner, CancellationToken.None)).Should().Equal(a.Tenant);
        (await members.ListTenantsOfSubjectAsync(a.Owner, activeOnly: true, CancellationToken.None)).Select(t => t.Id).Should().Equal(a.Tenant);
        (await members.ListMembershipsOfSubjectAsync(a.Owner, CancellationToken.None)).Should().ContainSingle()
            .Which.Membership.IsOwner.Should().BeTrue();
        factory.PinnedTo.Should().Equal(a.Owner, a.Owner, a.Owner);
    }

    [Fact]
    public async Task TenantPinned_ARequestIsReadUnderItsTenantsPinNotTheScopes()
    {
        var a = await SeedTenantAsync("d");
        var b = await SeedTenantAsync("e");
        var asA = new TenantMembershipRepository(new Factory(Context, a.Tenant));
        var asB = new TenantMembershipRepository(new Factory(Context, b.Tenant));
        var id = await asA.CreateRequestAsync(new NewMembershipRequest(a.Tenant, a.Orphan, "in", DateTime.UtcNow), CancellationToken.None);

        // membership_requests is tenant-scoped: read as tenant b, a's request does not exist, and the
        // pin to a is what reaches it from a scope that resolved b.
        await using (var db = Context(b.Tenant))
            (await db.MembershipRequests.AnyAsync(r => r.Id == id)).Should().BeFalse();
        (await asB.GetRequestAsync(a.Tenant, id, CancellationToken.None))!.Message.Should().Be("in");
        (await asB.ListPendingRequestsAsync(a.Tenant, CancellationToken.None)).Select(r => r.Id).Should().Equal(id);
        (await asB.ListPendingRequestsAsync(b.Tenant, CancellationToken.None)).Should().BeEmpty();
    }
}
