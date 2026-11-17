using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The holder of a tenant's API tokens, as <see cref="SubjectRepository"/> reads and creates it for
/// the scope's tenant.
/// </summary>
[Trait("Category", "Unit")]
public class SubjectRepositoryDeviceSubjectTests : IDisposable
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public SubjectRepositoryDeviceSubjectTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private SubjectRepository Repository(Guid tenant) => new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)));

    private int Holders()
    {
        using var db = _db.CreateContext();
        return db.Subjects.Count(s => s.Name == DeviceSubjectFilter.DeviceSubjectName);
    }

    [Fact]
    public async Task ATenantThatNeverIssuedATokenHasNoHolderAndAskingCreatesNone()
    {
        (await Repository(_tenant).FindDeviceSubjectAsync(CancellationToken.None)).Should().BeNull();

        Holders().Should().Be(0, "reading the token list must not write rows into a tenant that has never had a token");
    }

    [Fact]
    public async Task EnsuringTheHolderCreatesItOnceAndFindingReturnsIt()
    {
        var created = await Repository(_tenant).EnsureDeviceSubjectAsync(CancellationToken.None);
        var again = await Repository(_tenant).EnsureDeviceSubjectAsync(CancellationToken.None);

        again.Should().Be(created);
        (await Repository(_tenant).FindDeviceSubjectAsync(CancellationToken.None)).Should().Be(created);
        Holders().Should().Be(1);
    }

    [Fact]
    public async Task TheHolderIsASystemSubjectWithFullAccessMembershipOfTheScopesTenant()
    {
        var holder = await Repository(_tenant).EnsureDeviceSubjectAsync(CancellationToken.None);

        await using var db = _db.CreateContext();
        var subject = await db.Subjects.SingleAsync(s => s.Id == holder);
        subject.IsSystemSubject.Should().BeTrue();
        subject.IsPlatformAdmin.Should().BeFalse();
        var member = await db.TenantMembers.SingleAsync(m => m.SubjectId == holder);
        member.TenantId.Should().Be(_tenant);
        member.DirectPermissions.Should().Equal(Scope.FullAccess);
    }

    [Fact]
    public async Task EachTenantHasItsOwnHolderAndNeverFindsTheOthers()
    {
        var mine = await Repository(_tenant).EnsureDeviceSubjectAsync(CancellationToken.None);

        (await Repository(_otherTenant).FindDeviceSubjectAsync(CancellationToken.None)).Should().BeNull();

        var theirs = await Repository(_otherTenant).EnsureDeviceSubjectAsync(CancellationToken.None);
        theirs.Should().NotBe(mine);
        (await Repository(_tenant).FindDeviceSubjectAsync(CancellationToken.None)).Should().Be(mine);
        (await Repository(_otherTenant).FindDeviceSubjectAsync(CancellationToken.None)).Should().Be(theirs);
    }
}
