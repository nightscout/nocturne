using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Deleting and deactivating a subject over SQLite: neither may leave a tenant without an owner who
/// can sign in, and the refusal names the tenants. The owner lock is the Postgres tests' concern.
/// </summary>
public class SubjectOwnerGuardTests : IDisposable
{
    private readonly Guid _home = Guid.CreateVersion7();
    private readonly Guid _other = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly Dictionary<Guid, Guid> _ownerRoles = [];
    private readonly Dictionary<Guid, Guid> _caretakerRoles = [];

    public SubjectOwnerGuardTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_home, "home").SeedTenant(_other, "other");
        foreach (var tenant in new[] { _home, _other })
        {
            Members().SeedRolesAsync(tenant, CancellationToken.None).GetAwaiter().GetResult();
            _ownerRoles[tenant] = Members().FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, CancellationToken.None).GetAwaiter().GetResult()!.Value;
            _caretakerRoles[tenant] = Members().FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None).GetAwaiter().GetResult()!.Value;
        }
    }

    public void Dispose() => _db.Dispose();

    private TenantMembershipRepository Members() => new(new TestNocturneDbContextFactory(_db.CreateContext(_home)));

    private SubjectRepository Subjects() => new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)));

    private Task<Guid> SubjectAsync(string name, bool active = true) =>
        Subjects().CreateAsync(new NewSubject(name, IsActive: active), CancellationToken.None);

    private async Task JoinAsync(Guid tenant, Guid subject, bool owner) =>
        (await Members().AddMemberAsync(
            new NewTenantMember(tenant, subject, [owner ? _ownerRoles[tenant] : _caretakerRoles[tenant]]), CancellationToken.None))
            .Should().BeTrue();

    private async Task<bool> IsActiveAsync(Guid subject) => (await Subjects().GetAsync(subject, CancellationToken.None))!.IsActive;

    private async Task<bool> ExistsAsync(Guid subject) => await Subjects().GetAsync(subject, CancellationToken.None) is not null;

    private static IReadOnlyList<LastOwnerTenant> RefusedTenants(SubjectChange change) =>
        change.Should().BeOfType<SubjectChange.LastOwnerOf>().Subject.OwnedTenants;

    [Fact]
    public async Task ADeleteOfTheOnlyOwnerIsRefusedWhateverElseTheTenantHolds()
    {
        var owner = await SubjectAsync("Owner");
        await JoinAsync(_home, owner, owner: true);
        await JoinAsync(_home, await SubjectAsync("Carer"), owner: false);

        var refused = RefusedTenants(await Subjects().DeleteAsync(owner, CancellationToken.None));

        refused.Should().Equal(new LastOwnerTenant(_home, "home"));
        (await ExistsAsync(owner)).Should().BeTrue();
        (await Members().IsMemberAsync(_home, owner, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ADeleteOfTheOnlyMemberOfATenantIsRefusedToo()
    {
        var owner = await SubjectAsync("Alone");
        await JoinAsync(_home, owner, owner: true);

        RefusedTenants(await Subjects().DeleteAsync(owner, CancellationToken.None)).Should().Equal(new LastOwnerTenant(_home, "home"));
        (await ExistsAsync(owner)).Should().BeTrue();
    }

    [Fact]
    public async Task ADeleteNamesOnlyTheTenantsTheSubjectIsTheLastOwnerOf()
    {
        var subject = await SubjectAsync("Owner of two");
        var spare = await SubjectAsync("Spare");
        await JoinAsync(_home, subject, owner: true);
        await JoinAsync(_other, subject, owner: true);
        await JoinAsync(_other, spare, owner: true);

        RefusedTenants(await Subjects().DeleteAsync(subject, CancellationToken.None)).Should().Equal(new LastOwnerTenant(_home, "home"));

        await JoinAsync(_home, spare, owner: true);
        (await Subjects().DeleteAsync(subject, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
        (await Members().ListOwnersAsync(_other, CancellationToken.None)).Select(o => o.SubjectId).Should().Equal(spare);
    }

    [Fact]
    public async Task ADeleteGoesAheadWhileAnotherOwnerCanSignIn()
    {
        var leaving = await SubjectAsync("Leaving");
        var staying = await SubjectAsync("Staying");
        await JoinAsync(_home, leaving, owner: true);
        await JoinAsync(_home, staying, owner: true);

        (await Subjects().DeleteAsync(leaving, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();

        (await ExistsAsync(leaving)).Should().BeFalse();
        (await Members().ListOwnersAsync(_home, CancellationToken.None)).Select(o => o.SubjectId).Should().Equal(staying);
    }

    [Fact]
    public async Task ADeleteIsRefusedWhenTheOtherOwnerCannotSignIn()
    {
        var owner = await SubjectAsync("Owner");
        await JoinAsync(_home, owner, owner: true);
        await JoinAsync(_home, await SubjectAsync("Deactivated owner", active: false), owner: true);

        RefusedTenants(await Subjects().DeleteAsync(owner, CancellationToken.None)).Should().Equal(new LastOwnerTenant(_home, "home"));
    }

    [Fact]
    public async Task ADeleteOfAnOwnerWhoCannotSignInIsNotRefused()
    {
        var pending = await SubjectAsync("Pending", active: false);
        await JoinAsync(_home, pending, owner: true);

        (await Subjects().DeleteAsync(pending, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
        (await ExistsAsync(pending)).Should().BeFalse();
    }

    [Fact]
    public async Task ADeleteOfAMemberWhoIsNoOwnerGoesAhead()
    {
        await JoinAsync(_home, await SubjectAsync("Owner"), owner: true);
        var carer = await SubjectAsync("Carer");
        await JoinAsync(_home, carer, owner: false);

        (await Subjects().DeleteAsync(carer, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
        (await ExistsAsync(carer)).Should().BeFalse();
    }

    [Fact]
    public async Task ADeactivationOfTheOnlyOwnerIsRefusedAndLeavesThemActive()
    {
        var owner = await SubjectAsync("Owner");
        await JoinAsync(_home, owner, owner: true);

        RefusedTenants(await Subjects().UpdateAsync(owner, new SubjectEdit(IsActive: false), CancellationToken.None))
            .Should().Equal(new LastOwnerTenant(_home, "home"));

        (await IsActiveAsync(owner)).Should().BeTrue();
        (await Members().ListOwnersAsync(_home, CancellationToken.None)).Select(o => o.SubjectId).Should().Equal(owner);
    }

    [Fact]
    public async Task ADeactivationGoesAheadWhileAnotherOwnerCanSignInAndTheNextOneIsRefused()
    {
        var first = await SubjectAsync("First");
        var second = await SubjectAsync("Second");
        await JoinAsync(_home, first, owner: true);
        await JoinAsync(_home, second, owner: true);

        (await Subjects().UpdateAsync(first, new SubjectEdit(IsActive: false), CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
        (await IsActiveAsync(first)).Should().BeFalse();

        RefusedTenants(await Subjects().UpdateAsync(second, new SubjectEdit(IsActive: false), CancellationToken.None))
            .Should().Equal(new LastOwnerTenant(_home, "home"));
        (await IsActiveAsync(second)).Should().BeTrue();
    }

    [Fact]
    public async Task ADeactivationOfAnOwnerWhoAlreadyCannotSignInIsNotRefused()
    {
        var owner = await SubjectAsync("Gone", active: false);
        await JoinAsync(_home, owner, owner: true);

        (await Subjects().UpdateAsync(owner, new SubjectEdit(IsActive: false), CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
    }

    [Fact]
    public async Task EditsThatDoNotDeactivateTheOnlyOwnerAreNotRefused()
    {
        var owner = await SubjectAsync("Owner");
        await JoinAsync(_home, owner, owner: true);

        (await Subjects().UpdateAsync(owner, new SubjectEdit(Name: "Renamed", IsActive: true), CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
        (await Subjects().UpdateProfileAsync(owner, new SubjectProfile("Profile", null, null), CancellationToken.None))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ADeactivationOverAUnitIsRefusedInsideItsCommitAndChangesNothing()
    {
        var owner = await SubjectAsync("Owner");
        await JoinAsync(_home, owner, owner: true);

        await using var unit = DbUnitOfWork.Own(_db.CreateContext(Guid.Empty));
        var change = await unit.CommitAsync(ct =>
            new SubjectRepository(unit).UpdateAsync(owner, new SubjectEdit(IsActive: false), ct));

        RefusedTenants(change).Should().Equal(new LastOwnerTenant(_home, "home"));
        (await IsActiveAsync(owner)).Should().BeTrue();
    }
}
