using FluentAssertions;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.Core.Models.Tests.Identity;

public class MembershipSumTests
{
    private static readonly Guid[] RolePool = Enumerable.Range(1, 6).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
    private static readonly string[] PermissionPool = ["glucose.read", "treatments.read", "treatments.write", "alerts.read", "members.manage", "*"];
    private static readonly DateTime Base = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoReasons_SumToNull() =>
        MembershipSum.Of([]).Should().BeNull();

    public static TheoryData<MembershipGrant> SingleReasons() => new()
    {
        new MembershipGrant([RolePool[0]], null, false, null),
        new MembershipGrant([RolePool[0]], [], false, null),
        new MembershipGrant([RolePool[1], RolePool[0]], ["treatments.write", "glucose.read", "treatments.write"], false, null),
        new MembershipGrant([], null, false, null),
        new MembershipGrant([], [], true, null),
        new MembershipGrant([RolePool[2]], ["glucose.read"], true, Base),
        new MembershipGrant([RolePool[3], RolePool[4]], null, false, Base.AddDays(3)),
    };

    [Theory]
    [MemberData(nameof(SingleReasons))]
    public void OneReason_SumsToItselfExactly(MembershipGrant reason)
    {
        var sum = MembershipSum.Of([reason])!;

        sum.RoleIds.Should().Equal(reason.RoleIds);
        if (reason.DirectPermissions is null)
            sum.DirectPermissions.Should().BeNull();
        else
            sum.DirectPermissions.Should().NotBeNull().And.Equal(reason.DirectPermissions);
        sum.LimitTo24Hours.Should().Be(reason.LimitTo24Hours);
        sum.AccessEndsAt.Should().Be(reason.AccessEndsAt);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TheSum_HoldsEveryRoleAndPermissionOfEveryReasonAndNothingElse(int count)
    {
        var random = new Random(count);
        for (var i = 0; i < 500; i++)
        {
            var reasons = Reasons(random, count);
            var sum = MembershipSum.Of(reasons)!;

            sum.RoleIds.Should().OnlyHaveUniqueItems()
                .And.BeEquivalentTo(reasons.SelectMany(r => r.RoleIds).ToHashSet());

            var permissionLists = reasons.Where(r => r.DirectPermissions is not null).Select(r => r.DirectPermissions!).ToList();
            if (permissionLists.Count == 0)
            {
                sum.DirectPermissions.Should().BeNull();
                continue;
            }

            var firstSeen = new List<string>();
            foreach (var permission in permissionLists.SelectMany(p => p))
            {
                if (!firstSeen.Contains(permission))
                    firstSeen.Add(permission);
            }

            sum.DirectPermissions.Should().NotBeNull().And.Equal(firstSeen);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TheLimit_HoldsOnlyWhenEveryReasonHasIt_AndTheEndIsTheLatestUnlessOneHasNone(int count)
    {
        var random = new Random(10 + count);
        var sawLimit = false;
        var sawOpenEnd = false;
        var sawLatest = false;
        for (var i = 0; i < 500; i++)
        {
            var reasons = Reasons(random, count);
            var sum = MembershipSum.Of(reasons)!;

            var everyLimited = reasons.All(r => r.LimitTo24Hours);
            sum.LimitTo24Hours.Should().Be(everyLimited);
            sawLimit |= everyLimited;

            if (reasons.Any(r => r.AccessEndsAt is null))
            {
                sum.AccessEndsAt.Should().BeNull();
                sawOpenEnd = true;
            }
            else
            {
                var ends = reasons.Select(r => r.AccessEndsAt!.Value).ToList();
                sum.AccessEndsAt.Should().Be(ends.Max());
                sawLatest |= ends.Distinct().Count() > 1;
            }
        }

        sawLimit.Should().BeTrue();
        sawOpenEnd.Should().BeTrue();
        sawLatest.Should().BeTrue();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TheOrderOfTheReasons_ChangesNoRoleLimitOrEnd(int count)
    {
        var random = new Random(20 + count);
        for (var i = 0; i < 500; i++)
        {
            var reasons = Reasons(random, count);
            var shuffled = reasons.OrderBy(_ => random.Next()).ToList();

            var sum = MembershipSum.Of(reasons)!;
            var other = MembershipSum.Of(shuffled)!;

            other.RoleIds.Should().BeEquivalentTo(sum.RoleIds);
            other.LimitTo24Hours.Should().Be(sum.LimitTo24Hours);
            other.AccessEndsAt.Should().Be(sum.AccessEndsAt);
            (other.DirectPermissions is null).Should().Be(sum.DirectPermissions is null);
            if (sum.DirectPermissions is not null)
                other.DirectPermissions.Should().BeEquivalentTo(sum.DirectPermissions);
        }
    }

    [Fact]
    public void GroupingTheReasons_ChangesNothing()
    {
        var random = new Random(30);
        for (var i = 0; i < 500; i++)
        {
            var reasons = Reasons(random, 3);
            var sum = MembershipSum.Of(reasons)!;

            AssertSame(MembershipSum.Of([MembershipSum.Of([reasons[0], reasons[1]])!, reasons[2]])!, sum);
            AssertSame(MembershipSum.Of([reasons[0], MembershipSum.Of([reasons[1], reasons[2]])!])!, sum);
        }
    }

    [Fact]
    public void AnEmptyReasonBesideAFullOne_ChangesOnlyTheLimitAndTheEnd()
    {
        var random = new Random(40);
        for (var i = 0; i < 500; i++)
        {
            var full = Reason(random) with
            {
                RoleIds = Subset(random, RolePool, minimum: 1),
                DirectPermissions = Subset(random, PermissionPool, minimum: 1),
            };
            var empty = new MembershipGrant([], null, random.Next(2) == 0, End(random));

            foreach (var reasons in new MembershipGrant[][] { [full, empty], [empty, full] })
            {
                var sum = MembershipSum.Of(reasons)!;

                sum.RoleIds.Should().BeEquivalentTo(full.RoleIds);
                sum.DirectPermissions.Should().NotBeNull().And.Equal(full.DirectPermissions);
                sum.LimitTo24Hours.Should().Be(full.LimitTo24Hours && empty.LimitTo24Hours);
                sum.AccessEndsAt.Should().Be(
                    full.AccessEndsAt is null || empty.AccessEndsAt is null
                        ? null
                        : full.AccessEndsAt > empty.AccessEndsAt ? full.AccessEndsAt : empty.AccessEndsAt);
            }
        }
    }

    private static void AssertSame(MembershipGrant actual, MembershipGrant expected)
    {
        actual.RoleIds.Should().BeEquivalentTo(expected.RoleIds);
        if (expected.DirectPermissions is null)
            actual.DirectPermissions.Should().BeNull();
        else
            actual.DirectPermissions.Should().NotBeNull().And.Equal(expected.DirectPermissions);
        actual.LimitTo24Hours.Should().Be(expected.LimitTo24Hours);
        actual.AccessEndsAt.Should().Be(expected.AccessEndsAt);
    }

    private static List<MembershipGrant> Reasons(Random random, int count) =>
        Enumerable.Range(0, count).Select(_ => Reason(random)).ToList();

    private static MembershipGrant Reason(Random random) => new(
        Subset(random, RolePool, minimum: 0),
        random.Next(3) == 0 ? null : Enumerable.Range(0, random.Next(4)).Select(_ => PermissionPool[random.Next(PermissionPool.Length)]).ToList(),
        random.Next(2) == 0,
        End(random));

    private static DateTime? End(Random random) =>
        random.Next(3) == 0 ? null : Base.AddDays(random.Next(30));

    private static List<T> Subset<T>(Random random, T[] pool, int minimum) =>
        pool.OrderBy(_ => random.Next()).Take(random.Next(minimum, pool.Length + 1)).ToList();
}
