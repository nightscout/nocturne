using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
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
/// The pins of <see cref="TenantMembershipRepository"/> on PostgreSQL under the runtime role and
/// Row Level Security: the system-pinned setup-gate read answers for the tenant it pins from a
/// scope that reads as another tenant or as no one; a subject-pinned enumeration runs under the
/// <c>subject_pin</c> policy, which admits that subject's rows and hides every other; and a
/// tenant-pinned request read reaches a tenant-scoped table the scope's own tenant could not, and
/// the owner lock makes the second of two owners leaving at once read the first one's write.
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
        TestContexts.Context(database.AppConnectionString, tenant, npgsql: TestContexts.RetryingBriefly);

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
        var subjects = new SubjectRepository(factory, AccountLinkRoutes.Off);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var owner = await subjects.CreateAsync(new NewSubject($"{slug}-owner"), CancellationToken.None);
        var orphan = await subjects.CreateAsync(new NewSubject($"{slug}-orphan"), CancellationToken.None);
        await new PasskeyCredentialRepository(factory).CreateAsync(
            new NewPasskeyCredential(owner, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, DateTime.UtcNow),
            CancellationToken.None);
        await members.SeedRolesAsync(tenant, CancellationToken.None);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();
        (await members.AddMemberAsync(new NewTenantMember(tenant, orphan, [caretakerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();

        return new Seeded(tenant, owner, orphan);
    }

    [Fact]
    public async Task SystemPinned_TheSetupGateAnswersForThePinnedTenantFromAnyScope()
    {
        var a = await SeedTenantAsync("a");
        var b = await SeedTenantAsync("b");
        var readingAsB = new Factory(Context, b.Tenant);
        var readingAsNoOne = new Factory(Context, Guid.Empty);
        var fromB = new TenantMembershipRepository(readingAsB, AccountLinkRoutes.Off);
        var fromNoOne = new TenantMembershipRepository(readingAsNoOne, AccountLinkRoutes.Off);
        var unknown = Guid.NewGuid();

        (await fromB.ReadSetupGateAsync(a.Tenant, EndNow, CancellationToken.None))
            .Should().Be(new SetupGate(true, false), "the owner can sign in and recover the caretaker");
        (await fromNoOne.ReadSetupGateAsync(a.Tenant, EndNow, CancellationToken.None)).Should().Be(new SetupGate(true, false));
        (await fromNoOne.CountSetupCredentialsAsync(a.Tenant, CancellationToken.None)).Should().Be(new SetupCredentialCounts(2, 1, 0));
        (await fromNoOne.ListOrphanedMembersAsync(a.Tenant, CancellationToken.None)).Select(o => o.SubjectId).Should().Equal(a.Orphan);
        (await fromNoOne.ReadSetupGateAsync(unknown, EndNow, CancellationToken.None)).Should().BeNull();

        await using (var db = await new Factory(Context, a.Tenant).CreateForTenantAsync(a.Tenant))
        {
            await db.PasskeyCredentials.Where(c => c.SubjectId == a.Owner).ExecuteDeleteAsync();
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = a.Orphan, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1],
            });
            await db.SaveChangesAsync();
        }
        (await fromB.ReadSetupGateAsync(a.Tenant, EndNow, CancellationToken.None))
            .Should().Be(new SetupGate(true, true), "the only member administrator is the one locked out");

        readingAsB.SystemPinnedTo.Should().Equal(new[] { a.Tenant, a.Tenant }, "the gate read took the system pin");
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
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);

        // Read as the scope, the owner's membership row is hidden by subject_pin; the repository
        // reaches it only by pinning the read to that subject.
        (await members.IsMemberAsync(a.Tenant, a.Owner, DateTime.UtcNow, CancellationToken.None)).Should().BeFalse("the scope's reader is another subject");
        (await members.IsMemberAsync(a.Tenant, a.Orphan, DateTime.UtcNow, CancellationToken.None)).Should().BeTrue();
        (await members.ListTenantIdsOfSubjectAsync(a.Owner, CancellationToken.None)).Should().Equal(a.Tenant);
        (await members.ListTenantsOfSubjectAsync(a.Owner, activeOnly: true, DateTime.UtcNow, CancellationToken.None)).Select(t => t.Id).Should().Equal(a.Tenant);
        (await members.ListMembershipsOfSubjectAsync(a.Owner, DateTime.UtcNow, CancellationToken.None)).Should().ContainSingle()
            .Which.Membership.IsOwner.Should().BeTrue();
        factory.PinnedTo.Should().Equal(a.Owner, a.Owner, a.Owner);
    }

    [Fact]
    public async Task TenantPinned_ARequestIsReadUnderItsTenantsPinNotTheScopes()
    {
        var a = await SeedTenantAsync("d");
        var b = await SeedTenantAsync("e");
        var asA = new TenantMembershipRepository(new Factory(Context, a.Tenant), AccountLinkRoutes.Off);
        var asB = new TenantMembershipRepository(new Factory(Context, b.Tenant), AccountLinkRoutes.Off);
        var id = await asA.CreateRequestAsync(new NewMembershipRequest(a.Tenant, a.Orphan, "in", DateTime.UtcNow), CancellationToken.None);

        // membership_requests is tenant-scoped: read as tenant b, a's request does not exist, and the
        // pin to a is what reaches it from a scope that resolved b.
        await using (var db = Context(b.Tenant))
            (await db.MembershipRequests.AnyAsync(r => r.Id == id)).Should().BeFalse();
        (await asB.GetRequestAsync(a.Tenant, id, CancellationToken.None))!.Message.Should().Be("in");
        (await asB.ListPendingRequestsAsync(a.Tenant, CancellationToken.None)).Select(r => r.Id).Should().Equal(id);
        (await asB.ListPendingRequestsAsync(b.Tenant, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerLock_TheOnlyTwoOwnersLeavingAtOnceLeaveOneOwnerAndRefuseTheSecond()
    {
        var seeded = await SeedTenantAsync("f");
        var factory = new Factory(Context, seeded.Tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var ownerRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        var secondOwner = await new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject("f-second-owner"), CancellationToken.None);
        await GivePasskeyAsync(factory, secondOwner);
        await members.AddMemberAsync(new NewTenantMember(seeded.Tenant, secondOwner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None);
        var removedId = (await members.FindMemberAsync(seeded.Tenant, seeded.Owner, CancellationToken.None))!.Id;
        var steppingDownId = (await members.FindMemberAsync(seeded.Tenant, secondOwner, CancellationToken.None))!.Id;

        var staged = new TaskCompletionSource();
        var commit = new TaskCompletionSource();
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(factory, seeded.Tenant);
        var removal = unit.CommitAsync(
            async ct =>
            {
                var outcome = (await new TenantMembershipRepository(unit, AccountLinkRoutes.Off).RemoveMemberUnlessLastOwnerAsync(seeded.Tenant, removedId, MembershipEndHow.Removed, DateTime.UtcNow, ct)).Outcome;
                staged.TrySetResult();
                await commit.Task;
                await unit.Context.SaveChangesAsync(ct);
                return outcome;
            });
        await staged.Task;

        var stepDown = Task.Run(() => OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(
            factory, AccountLinkRoutes.Off, seeded.Tenant, steppingDownId, [ownerRole], [caretakerRole], DateTime.UtcNow, CancellationToken.None));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, stepDown);
        commit.SetResult();

        (await removal).Should().Be(OwnerGuardedChange.Changed);
        (await stepDown).Should().Be(OwnerGuardedChange.LastOwner, "it reads the committed removal under the lock");
        (await members.ListOwnersAsync(seeded.Tenant, CancellationToken.None)).Select(o => o.Id).Should().Equal(steppingDownId);
    }

    private static Task GivePasskeyAsync(INocturneDbContextFactory factory, Guid subject) =>
        new PasskeyCredentialRepository(factory).CreateAsync(
            new NewPasskeyCredential(subject, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, DateTime.UtcNow),
            CancellationToken.None);

    /// <summary>
    /// A tenant of two owners, the first leaving: its removal is staged on a unit and holds the owner
    /// lock while <paramref name="contend"/> runs against the second owner, which must wait for it.
    /// </summary>
    private async Task<(T Contender, OwnerGuardedChange Removal, Seeded Seeded, Guid SecondOwner)> RaceAgainstFirstOwnerLeavingAsync<T>(
        string slug, Func<INocturneDbContextFactory, Guid, Task<T>> contend)
    {
        var seeded = await SeedTenantAsync(slug);
        var factory = new Factory(Context, seeded.Tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var ownerRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        var secondOwner = await new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject($"{slug}-second-owner"), CancellationToken.None);
        await GivePasskeyAsync(factory, secondOwner);
        await members.AddMemberAsync(new NewTenantMember(seeded.Tenant, secondOwner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None);
        var removedId = (await members.FindMemberAsync(seeded.Tenant, seeded.Owner, CancellationToken.None))!.Id;

        var staged = new TaskCompletionSource();
        var commit = new TaskCompletionSource();
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(factory, seeded.Tenant);
        var removal = unit.CommitAsync(
            async ct =>
            {
                var outcome = (await new TenantMembershipRepository(unit, AccountLinkRoutes.Off).RemoveMemberUnlessLastOwnerAsync(seeded.Tenant, removedId, MembershipEndHow.Removed, DateTime.UtcNow, ct)).Outcome;
                staged.TrySetResult();
                await commit.Task;
                await unit.Context.SaveChangesAsync(ct);
                return outcome;
            });
        await staged.Task;

        var contender = Task.Run(() => contend(factory, secondOwner));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, contender);
        commit.SetResult();

        return (await contender, await removal, seeded, secondOwner);
    }

    [Fact]
    public async Task OwnerLock_DeletingTheSecondOwnerWaitsForTheFirstLeavingAndIsRefused()
    {
        var raced = await RaceAgainstFirstOwnerLeavingAsync("g", (factory, second) =>
            new SubjectRepository(factory, AccountLinkRoutes.Off).DeleteAsync(second, CancellationToken.None));

        raced.Removal.Should().Be(OwnerGuardedChange.Changed);
        raced.Contender.Should().BeOfType<SubjectChange.LastOwnerOf>()
            .Which.OwnedTenants.Select(t => t.TenantId).Should().Equal(raced.Seeded.Tenant);
        var factory = new Factory(Context, raced.Seeded.Tenant);
        (await new SubjectRepository(factory, AccountLinkRoutes.Off).GetAsync(raced.SecondOwner, CancellationToken.None)).Should().NotBeNull();
        (await new TenantMembershipRepository(factory, AccountLinkRoutes.Off).IsMemberAsync(raced.Seeded.Tenant, raced.SecondOwner, DateTime.UtcNow, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task OwnerLock_DeactivatingTheSecondOwnerWaitsForTheFirstLeavingAndIsRefused()
    {
        var raced = await RaceAgainstFirstOwnerLeavingAsync("h", (factory, second) =>
            new SubjectRepository(factory, AccountLinkRoutes.Off).UpdateAsync(second, new SubjectEdit(IsActive: false), CancellationToken.None));

        raced.Removal.Should().Be(OwnerGuardedChange.Changed);
        raced.Contender.Should().BeOfType<SubjectChange.LastOwnerOf>()
            .Which.OwnedTenants.Select(t => t.TenantId).Should().Equal(raced.Seeded.Tenant);
        var factory = new Factory(Context, raced.Seeded.Tenant);
        (await new SubjectRepository(factory, AccountLinkRoutes.Off).GetAsync(raced.SecondOwner, CancellationToken.None))!.IsActive.Should().BeTrue();
        (await new TenantMembershipRepository(factory, AccountLinkRoutes.Off).ListOwnersAsync(raced.Seeded.Tenant, CancellationToken.None))
            .Select(o => o.SubjectId).Should().Equal(raced.SecondOwner);
    }

    [Fact]
    public async Task OwnerLock_RemovingEveryMembershipWaitsForTheFirstLeavingAndNamesTheTenantItLeavesWithoutAnOwner()
    {
        var raced = await RaceAgainstFirstOwnerLeavingAsync("i", (factory, second) =>
            new TenantMembershipRepository(factory, AccountLinkRoutes.Off).RemoveMembershipsOfSubjectAsync(second, CancellationToken.None));

        raced.Removal.Should().Be(OwnerGuardedChange.Changed);
        raced.Contender.Removed.Should().Be(1);
        raced.Contender.TenantsLeftWithoutOwner.Select(t => t.TenantId).Should().Equal(raced.Seeded.Tenant);
        (await new TenantMembershipRepository(new Factory(Context, raced.Seeded.Tenant), AccountLinkRoutes.Off)
            .ListOwnersAsync(raced.Seeded.Tenant, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerLock_AnOwnerAddedWhileASubjectsMembershipsAreLockedWaitsForTheCommit()
    {
        var seeded = await SeedTenantAsync("l");
        var factory = new Factory(Context, seeded.Tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var ownerRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        var subject = await new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject("l-visitor"), CancellationToken.None);

        var locked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var holder = await DbUnitOfWork.OpenPlatformAsync(factory);
        var holding = holder.CommitAsync(async ct =>
        {
            var leftWithoutOwner = await holder.Context.LockTenantsOfSubjectAsync(subject, AccountLinkRoutes.Off, ct);
            locked.TrySetResult();
            await release.Task;
            return leftWithoutOwner;
        });
        await locked.Task;

        var adding = Task.Run(() => members.AddMemberAsync(new NewTenantMember(seeded.Tenant, subject, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, adding);
        release.SetResult();

        (await holding).Should().BeEmpty("the subject held no membership when its tenants were read");
        (await adding).Should().BeTrue();
    }

    [Fact]
    public async Task OwnerLock_ASubjectWithTwoTenantsTakesTheLowerTenantsLockBeforeWaitingOnTheHigher()
    {
        var a = await SeedTenantAsync("j");
        var b = await SeedTenantAsync("k");
        var (low, high) = string.CompareOrdinal(a.Tenant.ToString(), b.Tenant.ToString()) < 0 ? (a.Tenant, b.Tenant) : (b.Tenant, a.Tenant);
        var factory = new Factory(Context, a.Tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var subject = await new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject("j-visitor"), CancellationToken.None);
        foreach (var tenant in new[] { low, high })
        {
            var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
            await members.AddMemberAsync(new NewTenantMember(tenant, subject, [caretaker]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None);
        }

        var held = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var holder = await DbUnitOfWork.OpenPlatformAsync(factory);
        var holding = holder.CommitAsync(async ct =>
        {
            await holder.Context.LockTenantOwnersAsync(high, ct);
            held.TrySetResult();
            await release.Task;
        });
        await held.Task;

        var removal = Task.Run(() => members.RemoveMembershipsOfSubjectAsync(subject, CancellationToken.None));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, removal);

        await using var probe = await DbUnitOfWork.OpenPlatformAsync(factory);
        var lowIsHeld = !await probe.CommitAsync(ct => probe.Context.TryLockForTransactionAsync(TenantOwnerLock.Class, low, ct));
        release.SetResult();
        await holding;

        lowIsHeld.Should().BeTrue("the removal holds the lower tenant's lock while it waits for the higher's");
        (await removal).Removed.Should().Be(2);
    }

    private static readonly DateTime EndNow = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Ending(Seeded Seeded, Guid Guest, Guid MemberId, string TokenHash, string Username, string Email);

    /// <summary>
    /// <see cref="SeedTenantAsync"/> with a third person, a caretaker with a username, a confirmed
    /// address and a direct grant, whose membership ends at <paramref name="accessEndsAt"/>.
    /// </summary>
    private async Task<Ending> SeedEndingMemberAsync(string slug, DateTime? accessEndsAt)
    {
        var seeded = await SeedTenantAsync(slug);
        var factory = new Factory(Context, seeded.Tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var username = $"{slug}-{Guid.NewGuid():N}"[..20];
        var email = $"{username}@example.test";
        var guest = await new SubjectRepository(factory, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-guest", Username: username, Email: email), CancellationToken.None);
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE subjects SET email_confirmed_at = now() WHERE id = @id";
            cmd.Parameters.Add(new NpgsqlParameter("@id", guest));
            await cmd.ExecuteNonQueryAsync();
        }

        var caretaker = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(
            new NewTenantMember(seeded.Tenant, guest, [caretaker], AccessEndsAt: accessEndsAt) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();
        var memberId = (await members.FindMemberAsync(seeded.Tenant, guest, CancellationToken.None))!.Id;
        var tokenHash = Guid.NewGuid().ToString("N");
        await new OAuthGrantRepository(factory).CreateAsync(
            new NewOAuthGrant(null, guest, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "uploader", tokenHash, null, null, false),
            CancellationToken.None);
        return new Ending(seeded, guest, memberId, tokenHash, username, email);
    }

    /// <summary>Whether each read that decides access finds the guest's membership.</summary>
    private async Task<bool> AccessReadFindsAsync(string read, Ending e, DateTime now)
    {
        var tenant = e.Seeded.Tenant;
        var factory = new Factory(Context, tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var subjects = new SubjectRepository(factory, AccountLinkRoutes.Off);
        var none = CancellationToken.None;
        return read switch
        {
            "GetMemberAccess" => await members.GetMemberAccessAsync(tenant, e.Guest, now, none) is not null,
            "IsMember" => await members.IsMemberAsync(tenant, e.Guest, now, none),
            "GetMemberRoleNames" => (await members.GetMemberRoleNamesAsync(tenant, e.Guest, now, none)).Count > 0,
            "ListLivePeople" => (await members.ListLivePeopleAsync(tenant, now, none)).Any(m => m.SubjectId == e.Guest),
            "ReadSubjectMembership" => (await members.ReadSubjectMembershipAsync(e.Guest, tenant, now, none))!.Membership is not null,
            "ListTenantsOfSubject" => (await members.ListTenantsOfSubjectAsync(e.Guest, activeOnly: true, now, none)).Any(t => t.Id == tenant),
            "ListMembershipsOfSubject" => (await members.ListMembershipsOfSubjectAsync(e.Guest, now, none)).Any(m => m.Tenant.Id == tenant),
            "FindLiveDirectGrant" => (await new OAuthGrantRepository(factory).FindLiveDirectGrantAsync(
                tenant, new DirectGrantLookup.ByTokenHash([e.TokenHash]), now, none))!.Membership is not null,
            "FindMemberByUsername" => await subjects.FindMemberByUsernameAsync(tenant, e.Username, activeOnly: true, now, none) is not null,
            "FindMembersByConfirmedEmail" => (await subjects.FindMembersByConfirmedEmailAsync(tenant, e.Email, now, none)).Any(s => s.Id == e.Guest),
            _ => throw new ArgumentOutOfRangeException(nameof(read)),
        };
    }

    public static TheoryData<string, int, bool> AccessReadsAroundTheEnd()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var read in new[]
                 {
                     "GetMemberAccess", "IsMember", "GetMemberRoleNames", "ListLivePeople", "ReadSubjectMembership",
                     "ListTenantsOfSubject", "ListMembershipsOfSubject", "FindLiveDirectGrant", "FindMemberByUsername",
                     "FindMembersByConfirmedEmail",
                 })
        {
            data.Add(read, -1, false);
            data.Add(read, 0, false);
            data.Add(read, 1, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AccessReadsAroundTheEnd))]
    public async Task AccessEnd_EveryAccessReadTreatsAnEndedMembershipAsAbsent(string read, int endsInSeconds, bool present)
    {
        var ending = await SeedEndingMemberAsync("end", EndNow.AddSeconds(endsInSeconds));

        (await AccessReadFindsAsync(read, ending, EndNow)).Should().Be(present,
            "a membership that ends at {0:O} has {1}ended at {2:O}", EndNow.AddSeconds(endsInSeconds), present ? "not " : "", EndNow);
    }

    [Fact]
    public async Task AccessEnd_TheManagementReadsStillReturnAnEndedMembershipWithItsDate()
    {
        var end = EndNow.AddDays(-1);
        var ending = await SeedEndingMemberAsync("mgmt", end);
        var members = new TenantMembershipRepository(new Factory(Context, ending.Seeded.Tenant), AccountLinkRoutes.Off);

        (await members.ListMembersAsync(ending.Seeded.Tenant, CancellationToken.None))
            .Should().ContainSingle(m => m.SubjectId == ending.Guest).Which.AccessEndsAt.Should().Be(end);
        (await members.GetMemberAsync(ending.Seeded.Tenant, ending.MemberId, CancellationToken.None))!.AccessEndsAt.Should().Be(end);
        (await members.FindMemberAsync(ending.Seeded.Tenant, ending.Guest, CancellationToken.None))!.AccessEndsAt.Should().Be(end);
    }

    [Fact]
    public async Task AccessEnd_TheSetupGateAndTheOwnerReadsDoNotLookAtTheDate()
    {
        var ending = await SeedEndingMemberAsync("gate", EndNow.AddDays(-1));
        var tenant = ending.Seeded.Tenant;
        var members = new TenantMembershipRepository(new Factory(Context, tenant), AccountLinkRoutes.Off);

        (await members.CountSetupCredentialsAsync(tenant, CancellationToken.None)).Members.Should().Be(3);
        (await members.ListPeopleAsync(tenant, CancellationToken.None)).Select(m => m.SubjectId).Should().Contain(ending.Guest);
        (await members.GetOwnerSubjectIdsAsync(tenant, CancellationToken.None)).Should().Equal(ending.Seeded.Owner);
    }

    [Fact]
    public async Task AccessEnd_TheDirectGrantJoinRefusesAnEndedMembersGrantAndAcceptsItOnceTheEndMovesLater()
    {
        var ending = await SeedEndingMemberAsync("grant", EndNow.AddMinutes(-1));
        var tenant = ending.Seeded.Tenant;
        var contexts = new Factory(Context, tenant);

        (await AccessReadFindsAsync("FindLiveDirectGrant", ending, EndNow)).Should().BeFalse();

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, ending.MemberId, EndNow.AddDays(7), EndNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.Changed);

        (await AccessReadFindsAsync("FindLiveDirectGrant", ending, EndNow)).Should().BeTrue("nothing was revoked when the date passed");
    }

    [Fact]
    public async Task AccessEnd_AnOwnerCannotHaveAnEndAndAMemberWithOneCannotBeMadeOwner()
    {
        var ending = await SeedEndingMemberAsync("owner", EndNow.AddDays(7));
        var tenant = ending.Seeded.Tenant;
        var contexts = new Factory(Context, tenant);
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        var ownerMembership = (await members.FindMemberAsync(tenant, ending.Seeded.Owner, CancellationToken.None))!;
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, ownerMembership.Id, EndNow.AddDays(1), EndNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.OwnerAccessEnds);
        (await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, ending.MemberId, [], [ownerRole], EndNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.OwnerAccessEnds);

        (await members.FindMemberAsync(tenant, ending.Seeded.Owner, CancellationToken.None))!.AccessEndsAt.Should().BeNull();
        (await members.GetMemberAsync(tenant, ending.MemberId, CancellationToken.None))!.IsOwner.Should().BeFalse();

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, ending.MemberId, null, EndNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.Changed);
        (await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, ending.MemberId, [], [ownerRole], EndNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.Changed, "the date was cleared first");
    }

    /// <summary>The <c>access_ends_at</c> each audit row of the subject's end date names, in order, with its <c>kind</c>.</summary>
    private async Task<List<string>> AccessEndAuditAsync(Guid tenant, Guid subject)
    {
        await using var db = Context(tenant);
        var details = await db.AuthAuditLog.AsNoTracking()
            .Where(r => r.TenantId == tenant && r.TargetSubjectId == subject && r.EventType == AuthAuditEventType.AccessEndChanged)
            .OrderBy(r => r.Id)
            .Select(r => r.DetailsJson!)
            .ToListAsync();
        return details.Select(json =>
        {
            using var row = System.Text.Json.JsonDocument.Parse(json);
            var end = row.RootElement.GetProperty("access_ends_at");
            return $"{row.RootElement.GetProperty("kind").GetString()} {(end.ValueKind == System.Text.Json.JsonValueKind.Null ? "null" : end.GetString())}";
        }).ToList();
    }

    [Fact]
    public async Task AccessEnd_SettingChangingAndClearingTheDateEachWriteOneAuditRowAndANoOpWritesNone()
    {
        var ending = await SeedEndingMemberAsync("audit", accessEndsAt: null);
        var tenant = ending.Seeded.Tenant;
        var contexts = new Factory(Context, tenant);
        var first = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        Task SetEndAsync(DateTime? end) =>
            OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, ending.MemberId, end, EndNow, CancellationToken.None);

        (await AccessEndAuditAsync(tenant, ending.Guest)).Should().BeEmpty("the membership was created with no end");

        await SetEndAsync(first);
        await SetEndAsync(first);
        await SetEndAsync(second);
        await SetEndAsync(null);
        await SetEndAsync(null);

        (await AccessEndAuditAsync(tenant, ending.Guest)).Should().Equal(
            "tenant 2026-12-01T00:00:00.0000000Z",
            "tenant 2026-12-15T00:00:00.0000000Z",
            "tenant null");
    }

    [Fact]
    public async Task AccessEnd_AMembershipCreatedWithADateWritesItsAuditRow()
    {
        var end = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var ending = await SeedEndingMemberAsync("created", end);

        (await AccessEndAuditAsync(ending.Seeded.Tenant, ending.Guest)).Should().Equal(
            "tenant 2026-12-01T00:00:00.0000000Z");
    }

    /// <summary>
    /// A tenant whose owner cannot sign in and whose only other administrator holds a passkey and a
    /// membership ending at <paramref name="administratorAccessEndsAt"/>.
    /// </summary>
    private async Task<(Guid Tenant, Guid AdministratorMember)> SeedStuckOwnerWithAnAdministratorAsync(
        string slug, DateTime administratorAccessEndsAt)
    {
        var seeded = await SeedTenantAsync(slug);
        var factory = new Factory(Context, seeded.Tenant);
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        await using (var db = await factory.CreateForTenantAsync(seeded.Tenant))
        {
            await db.PasskeyCredentials.Where(c => c.SubjectId == seeded.Owner).ExecuteDeleteAsync();
        }

        var administrator = await new SubjectRepository(factory, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-admin"), CancellationToken.None);
        await new PasskeyCredentialRepository(factory).CreateAsync(
            new NewPasskeyCredential(administrator, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, DateTime.UtcNow),
            CancellationToken.None);
        var adminRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Admin, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(
            new NewTenantMember(seeded.Tenant, administrator, [adminRole], AccessEndsAt: administratorAccessEndsAt) { How = MembershipJoinHow.Direct, By = null },
            CancellationToken.None)).Should().BeTrue();
        return (seeded.Tenant, (await members.FindMemberAsync(seeded.Tenant, administrator, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task AccessEnd_AnAdministratorWhoseAccessHasEndedDoesNotKeepTheSiteOutOfRecoveryMode()
    {
        var (tenant, administratorMember) = await SeedStuckOwnerWithAnAdministratorAsync("recover", EndNow.AddSeconds(-1));
        var contexts = new Factory(Context, tenant);
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);

        (await members.ReadSetupGateAsync(tenant, EndNow, CancellationToken.None))
            .Should().Be(new SetupGate(HasCredentials: true, RecoveryMode: true), "the ended administrator cannot sign in");

        await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(contexts, AccountLinkRoutes.Off, tenant, administratorMember, EndNow.AddDays(1), EndNow, CancellationToken.None);

        (await members.ReadSetupGateAsync(tenant, EndNow, CancellationToken.None))
            .Should().Be(new SetupGate(HasCredentials: true, RecoveryMode: false), "the administrator can sign in again");
    }

    [Fact]
    public async Task AccessEnd_AnEndedMembersCredentialStillCountsSoTheSiteIsNotSentBackToSetup()
    {
        var ending = await SeedEndingMemberAsync("setup", EndNow.AddDays(-1));
        var tenant = ending.Seeded.Tenant;
        var members = new TenantMembershipRepository(new Factory(Context, tenant), AccountLinkRoutes.Off);
        await using (var db = await new Factory(Context, tenant).CreateForTenantAsync(tenant))
        {
            await db.PasskeyCredentials.Where(c => c.SubjectId == ending.Seeded.Owner).ExecuteDeleteAsync();
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = ending.Guest, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1],
            });
            await db.SaveChangesAsync();
        }

        (await members.ReadSetupGateAsync(tenant, EndNow, CancellationToken.None))!.HasCredentials
            .Should().BeTrue("whether any member holds a credential reads ended memberships too");
    }
}
