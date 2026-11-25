using FluentAssertions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Owner A, who can sign in or cannot, and owner B in one of five shapes. A change that stops A being
/// an owner is refused when A is the only active owner, or when A can sign in and B cannot; so a site
/// that serves is never put into recovery mode by any of them, and an owner who cannot sign in can
/// still be taken off.
/// </summary>
public class LastOwnerWhoCanSignInTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private Guid _ownerRole;
    private Guid _caretakerRole;

    public LastOwnerWhoCanSignInTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "home");
    }

    public void Dispose() => _db.Dispose();

    public enum Path { Leave, Remove, RemoveOwnerRole, Deactivate, Delete }

    public enum OtherOwner
    {
        None,
        CannotSignIn,
        CanSignIn,
        HoldsAConfirmedAddressWithDelivery,
        HoldsAConfirmedAddressWithoutDelivery,
    }

    private TestNocturneDbContextFactory Contexts() => new(_db.CreateContext(_tenant));

    private TenantMembershipRepository Members(IAccountLinkRoute route) => new(Contexts(), route);

    private SubjectRepository Subjects(IAccountLinkRoute route) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)), route);

    private async Task<(Guid A, Guid AMember)> ArrangeAsync(bool aSignsIn, OtherOwner b, IAccountLinkRoute route)
    {
        var members = Members(route);
        await members.SeedRolesAsync(_tenant, CancellationToken.None);
        _ownerRole = (await members.FindRoleIdBySlugAsync(_tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        _caretakerRole = (await members.FindRoleIdBySlugAsync(_tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;

        var a = await JoinOwnerAsync("A", route);
        if (aSignsIn)
            AddPasskey(a);
        if (b == OtherOwner.None)
            return (a, (await members.FindMemberAsync(_tenant, a, CancellationToken.None))!.Id);

        var bId = await JoinOwnerAsync("B", route);
        switch (b)
        {
            case OtherOwner.CanSignIn:
                AddPasskey(bId);
                break;
            case OtherOwner.HoldsAConfirmedAddressWithDelivery:
            case OtherOwner.HoldsAConfirmedAddressWithoutDelivery:
                using (var db = _db.CreateContext())
                {
                    var row = db.Subjects.Single(s => s.Id == bId);
                    row.SetEmail("b@example.test");
                    row.ConfirmEmail("b@example.test", Now);
                    db.SaveChanges();
                }
                break;
        }

        return (a, (await members.FindMemberAsync(_tenant, a, CancellationToken.None))!.Id);
    }

    private async Task<Guid> JoinOwnerAsync(string name, IAccountLinkRoute route)
    {
        var subject = await Subjects(route).CreateAsync(new NewSubject(name), CancellationToken.None);
        (await Members(route).AddMemberAsync(new NewTenantMember(_tenant, subject, [_ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();
        return subject;
    }

    private void AddPasskey(Guid subject)
    {
        using var db = _db.CreateContext();
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = subject, CredentialId = subject.ToByteArray(), PublicKey = [1],
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Runs <paramref name="path"/> on A and answers whether it went ahead. Leaving every tenant is
    /// not refused by the repository; it names the tenants its caller then refuses it for.
    /// </summary>
    private async Task<bool> ActAsync(Path path, Guid a, Guid aMember, IAccountLinkRoute route) => path switch
    {
        Path.Leave => (await Members(route).RemoveMembershipsOfSubjectAsync(a, CancellationToken.None)).TenantsLeftWithoutOwner.Count == 0,
        Path.Remove => (await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(
            Contexts(), route, _tenant, aMember, MembershipEndHow.Removed, Now, CancellationToken.None)).Outcome == OwnerGuardedChange.Changed,
        Path.RemoveOwnerRole => await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(
            Contexts(), route, _tenant, aMember, [_ownerRole], [_caretakerRole], Now, CancellationToken.None) == OwnerGuardedChange.Changed,
        Path.Deactivate => await Subjects(route).UpdateAsync(a, new SubjectEdit(IsActive: false), CancellationToken.None) is SubjectChange.Changed,
        Path.Delete => await Subjects(route).DeleteAsync(a, CancellationToken.None) is SubjectChange.Changed,
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    public static TheoryData<Path, bool, OtherOwner, bool> Cases()
    {
        var data = new TheoryData<Path, bool, OtherOwner, bool>();
        foreach (var path in Enum.GetValues<Path>())
        {
            data.Add(path, true, OtherOwner.None, false);
            data.Add(path, true, OtherOwner.CannotSignIn, false);
            data.Add(path, true, OtherOwner.CanSignIn, true);
            data.Add(path, true, OtherOwner.HoldsAConfirmedAddressWithDelivery, true);
            data.Add(path, true, OtherOwner.HoldsAConfirmedAddressWithoutDelivery, false);
            data.Add(path, false, OtherOwner.None, false);
            data.Add(path, false, OtherOwner.CannotSignIn, true);
            data.Add(path, false, OtherOwner.CanSignIn, true);
        }
        return data;
    }

    private static IAccountLinkRoute RouteFor(OtherOwner b) =>
        AccountLinkRoutes.Of(b == OtherOwner.HoldsAConfirmedAddressWithDelivery);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AChangeThatEndsAsOwnershipIsRefusedOnlyForTheOnlyOwnerOrTheLastOneWhoCanSignIn(
        Path path, bool aSignsIn, OtherOwner b, bool allowed)
    {
        var route = RouteFor(b);
        var (a, aMember) = await ArrangeAsync(aSignsIn, b, route);
        var owners = await Members(route).ListOwnersAsync(_tenant, CancellationToken.None);
        var stuck = (await Members(route).ListOrphanedMembersAsync(_tenant, CancellationToken.None)).Select(o => o.SubjectId).ToHashSet();

        (await ActAsync(path, a, aMember, route)).Should().Be(allowed);

        if (!allowed)
        {
            var onlyOwner = owners.Count == 1;
            var lastWhoCanSignIn = !stuck.Contains(a) && owners.Where(o => o.SubjectId != a).All(o => stuck.Contains(o.SubjectId));
            (onlyOwner || lastWhoCanSignIn).Should().BeTrue($"the refusal says: {TenantOwnerFilter.LastOwnerRefusal}");
        }
    }

    public static TheoryData<bool, OtherOwner> ServingSites() => new()
    {
        { true, OtherOwner.None },
        { true, OtherOwner.CannotSignIn },
        { true, OtherOwner.CanSignIn },
        { true, OtherOwner.HoldsAConfirmedAddressWithDelivery },
        { true, OtherOwner.HoldsAConfirmedAddressWithoutDelivery },
        { false, OtherOwner.CanSignIn },
        { false, OtherOwner.HoldsAConfirmedAddressWithDelivery },
    };

    [Theory]
    [MemberData(nameof(ServingSites))]
    public async Task NoSequenceOfGuardedChangesPutsAServingSiteIntoRecoveryMode(bool aSignsIn, OtherOwner b)
    {
        var route = RouteFor(b);
        var (a, aMember) = await ArrangeAsync(aSignsIn, b, route);
        (await Members(route).ReadSetupGateAsync(_tenant, Now, CancellationToken.None))!.RecoveryMode.Should().BeFalse("the site serves before");

        foreach (var path in new[] { Path.RemoveOwnerRole, Path.Remove, Path.Deactivate, Path.Delete })
        {
            await ActAsync(path, a, aMember, route);
            (await Members(route).ReadSetupGateAsync(_tenant, Now, CancellationToken.None))?.RecoveryMode
                .Should().NotBe(true, $"after {path} the site still serves");
        }
    }
}
