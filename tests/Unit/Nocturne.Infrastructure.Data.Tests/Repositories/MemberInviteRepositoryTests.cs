using FluentAssertions;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="MemberInviteRepository"/> over SQLite, which runs the conditional update the use claim
/// is made of: a single-use invite admits one accept, a lookup is bounded by the tenant named, and a
/// revoke reports what it found.
/// </summary>
public class MemberInviteRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly Guid _owner = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public MemberInviteRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant).SeedTenant(_otherTenant, "other");
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _owner, Name = "Owner" });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private MemberInviteRepository Repository() => new(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)));

    private NewMemberInvite Invite(string hash, int? maxUses = 1, DateTime? expiresAt = null) =>
        new(_tenant, _owner, hash, [Guid.CreateVersion7()], ["glucose.read"], "Family", LimitTo24Hours: true,
            expiresAt ?? Now.AddDays(7), maxUses, Now);

    [Fact]
    public async Task ASingleUseInviteIsClaimedOnce()
    {
        var repo = Repository();
        var id = await repo.CreateAsync(Invite("hash"), CancellationToken.None);

        (await repo.TryClaimUseAsync(id, CancellationToken.None)).Should().BeTrue();
        (await repo.TryClaimUseAsync(id, CancellationToken.None)).Should().BeFalse();

        var stored = await repo.FindByTokenHashAsync("hash", _tenant, CancellationToken.None);
        stored!.UseCount.Should().Be(1);
        stored.IsExhausted.Should().BeTrue();
        stored.IsValidAt(Now).Should().BeFalse();
        stored.TenantDisplayName.Should().BeEmpty("the seeded tenant carries no display name");
        stored.CreatedByName.Should().Be("Owner");
        stored.Uses.Should().BeEmpty("the token lookup does not read who joined");
    }

    [Fact]
    public async Task ATokenIsMatchedOnTheTenantNamed()
    {
        var repo = Repository();
        await repo.CreateAsync(Invite("hash", maxUses: null), CancellationToken.None);

        (await repo.FindByTokenHashAsync("hash", _tenant, CancellationToken.None)).Should().NotBeNull();
        (await repo.FindByTokenHashAsync("hash", _otherTenant, CancellationToken.None)).Should().BeNull();
        (await repo.FindByTokenHashAsync("missing", _tenant, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ARevokeReportsWhatItFoundAndTheSharingTestSeesWhatCounts()
    {
        var repo = Repository();
        var id = await repo.CreateAsync(Invite("hash"), CancellationToken.None);
        (await repo.AnyUsedOrLiveAsync(Now, CancellationToken.None)).Should().BeTrue();

        (await repo.RevokeAsync(id, _otherTenant, Now, CancellationToken.None)).Should().Be(MemberInviteRevocation.NotFound);
        (await repo.RevokeAsync(id, _tenant, Now, CancellationToken.None)).Should().Be(MemberInviteRevocation.Revoked);
        (await repo.RevokeAsync(id, _tenant, Now.AddHours(1), CancellationToken.None)).Should().Be(MemberInviteRevocation.AlreadyRevoked);

        var invites = await repo.GetForTenantAsync(_tenant, CancellationToken.None);
        invites.Should().ContainSingle().Which.RevokedAt.Should().Be(Now);
        (await repo.AnyUsedOrLiveAsync(Now, CancellationToken.None)).Should().BeFalse("a revoked, unused invite never counted");

        await repo.CreateAsync(Invite("expired", maxUses: null, expiresAt: Now), CancellationToken.None);
        (await repo.AnyUsedOrLiveAsync(Now, CancellationToken.None)).Should().BeFalse("the expiry instant itself is expired");
        (await repo.AnyUsedOrLiveAsync(Now.AddTicks(-1), CancellationToken.None)).Should().BeTrue();
    }
}
