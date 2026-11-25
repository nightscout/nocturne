using FluentAssertions;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.Core.Models.Tests.Identity;

public class MembershipSumInForceTests
{
    private static readonly Guid[] RolePool = Enumerable.Range(1, 6).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
    private static readonly string[] PermissionPool = ["glucose.read", "treatments.read", "treatments.write", "alerts.read", "members.manage", "*"];
    private static readonly DateTime Base = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Administrator = RolePool[0];
    private static readonly Guid Viewer = RolePool[1];
    private static readonly DateTime CampEnds = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoReasons_ProjectToNull() =>
        MembershipSum.InForceAt([], Base).Should().BeNull();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OneReason_ProjectsToItsOwnSumAtAnyInstant_WithNothingToLapse(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 1000; i++)
        {
            var reason = Reason(random);
            var now = Instant(random);
            var because = $"seed {seed}, case {i}: {reason} at {now:O}";

            var projection = MembershipSum.InForceAt([reason], now)!;

            projection.Grant.Should().BeSameAs(MembershipSum.Of([reason]), because);
            projection.NextReasonLapseAt.Should().BeNull(because);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void SeveralReasons_ProjectToTheSumOfThoseInForce_AndLapseAtTheEarliestEndBeforeTheRows(int count)
    {
        var seed = 100 + count;
        var random = new Random(seed);
        for (var i = 0; i < 1000; i++)
        {
            var reasons = Enumerable.Range(0, count).Select(_ => Reason(random)).ToList();
            var now = Instant(random);
            var because = $"seed {seed}, case {i} at {now:O}";

            var projection = MembershipSum.InForceAt(reasons, now)!;

            var inForce = reasons.Where(r => r.AccessEndsAt is null || r.AccessEndsAt > now).ToList();
            AssertSameGrant(projection.Grant, MembershipSum.Of(inForce.Count == 0 ? reasons : inForce)!, because);

            var rowEnd = projection.Grant.AccessEndsAt;
            var earlier = inForce
                .Where(r => r.AccessEndsAt is { } end && (rowEnd is null || end < rowEnd))
                .Select(r => r.AccessEndsAt!.Value)
                .ToList();
            if (earlier.Count == 0)
                projection.NextReasonLapseAt.Should().BeNull(because);
            else
                projection.NextReasonLapseAt.Should().Be(earlier.Min(), because);
        }
    }

    [Fact]
    public void AnAdministratorThroughACamp_IsOnlyAViewerOnceTheCampEnds()
    {
        var camp = new MembershipGrant([Administrator], null, LimitTo24Hours: false, CampEnds);
        var family = new MembershipGrant([Viewer], null, LimitTo24Hours: false, AccessEndsAt: null);

        var during = MembershipSum.InForceAt([family, camp], CampEnds.AddDays(-1))!;
        during.Grant.RoleIds.Should().BeEquivalentTo([Administrator, Viewer]);
        during.Grant.AccessEndsAt.Should().BeNull();
        during.NextReasonLapseAt.Should().Be(CampEnds);

        var after = MembershipSum.InForceAt([family, camp], CampEnds.AddDays(1))!;
        after.Grant.RoleIds.Should().Equal(Viewer);
        after.NextReasonLapseAt.Should().BeNull();
    }

    [Fact]
    public void TheRowIsLimitedOnlyWhileEveryReasonInForceIsLimited()
    {
        var direct = new MembershipGrant([Viewer], null, LimitTo24Hours: false, CampEnds);
        var limited = new MembershipGrant([Viewer], null, LimitTo24Hours: true, AccessEndsAt: null);

        var before = MembershipSum.InForceAt([direct, limited], CampEnds.AddDays(-1))!;
        before.Grant.LimitTo24Hours.Should().BeFalse();
        before.NextReasonLapseAt.Should().Be(CampEnds);

        var after = MembershipSum.InForceAt([direct, limited], CampEnds)!;
        after.Grant.LimitTo24Hours.Should().BeTrue();
        after.NextReasonLapseAt.Should().BeNull();
    }

    [Fact]
    public void AReasonEndingWithTheRow_IsNoLapse()
    {
        var first = new MembershipGrant([Viewer], null, false, CampEnds);
        var second = new MembershipGrant([Administrator], null, false, CampEnds);

        var projection = MembershipSum.InForceAt([first, second], CampEnds.AddDays(-1))!;

        projection.Grant.AccessEndsAt.Should().Be(CampEnds);
        projection.NextReasonLapseAt.Should().BeNull();
    }

    [Fact]
    public void WithNoReasonInForce_TheRowIsTheSumOfAllOfThem_AndHasEnded()
    {
        var first = new MembershipGrant([Viewer], null, false, CampEnds);
        var second = new MembershipGrant([Administrator], null, false, CampEnds.AddDays(-3));

        var projection = MembershipSum.InForceAt([first, second], CampEnds.AddDays(1))!;

        projection.Grant.RoleIds.Should().BeEquivalentTo([Administrator, Viewer]);
        projection.Grant.AccessEndsAt.Should().Be(CampEnds);
        projection.NextReasonLapseAt.Should().BeNull();
    }

    private static void AssertSameGrant(MembershipGrant actual, MembershipGrant expected, string because)
    {
        actual.RoleIds.Should().BeEquivalentTo(expected.RoleIds, because);
        if (expected.DirectPermissions is null)
            actual.DirectPermissions.Should().BeNull(because);
        else
            actual.DirectPermissions.Should().NotBeNull(because).And.Equal(expected.DirectPermissions);
        actual.LimitTo24Hours.Should().Be(expected.LimitTo24Hours, because);
        actual.AccessEndsAt.Should().Be(expected.AccessEndsAt, because);
    }

    private static MembershipGrant Reason(Random random) => new(
        RolePool.OrderBy(_ => random.Next()).Take(random.Next(RolePool.Length + 1)).ToList(),
        random.Next(3) == 0 ? null : Enumerable.Range(0, random.Next(4)).Select(_ => PermissionPool[random.Next(PermissionPool.Length)]).ToList(),
        random.Next(2) == 0,
        random.Next(3) == 0 ? null : Base.AddDays(random.Next(30)));

    private static DateTime Instant(Random random) => Base.AddHours(random.Next(-24, 32 * 24));
}
