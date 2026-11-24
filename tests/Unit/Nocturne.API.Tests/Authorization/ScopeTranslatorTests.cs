using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class ScopeTranslatorTests
{
    [Fact]
    public void FromPermissions_WildcardAdmin_GrantsFullAccess()
    {
        var permissions = new[] { "*" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.FullAccess, scopes);
        // Full access should also include all individual scopes
        Assert.Contains(Scope.GlucoseRead, scopes);
        Assert.Contains(Scope.TreatmentsReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_AdminRole_GrantsFullAccess()
    {
        var permissions = new[] { "admin" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.FullAccess, scopes);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("Admin")]
    [InlineData("READABLE")]
    [InlineData("Readable")]
    public void FromPermissions_SeededRoleNamesMatchExactly(string name)
    {
        ScopeTranslator.FromPermissions([name]).Should().BeEmpty();
    }

    [Fact]
    public void FromPermissions_PermissionStringsStillIgnoreCase()
    {
        ScopeTranslator.FromPermissions(["API:ENTRIES:READ"]).Should().Equal(Scope.GlucoseRead);
    }

    [Fact]
    public void FromPermissions_EntriesRead_MapsToEntriesRead()
    {
        var permissions = new[] { "api:entries:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.GlucoseRead, scopes);
        Assert.DoesNotContain(Scope.GlucoseReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_EntriesCreate_MapsToEntriesReadWrite()
    {
        var permissions = new[] { "api:entries:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.GlucoseReadWrite, scopes);
    }

    [Theory]
    [InlineData("api:entries:*", Scope.GlucoseReadWrite)]
    [InlineData("api:treatments:*", Scope.TreatmentsReadWrite)]
    [InlineData("api:devicestatus:*", Scope.DevicesReadWrite)]
    [InlineData("api:food:*", Scope.FoodReadWrite)]
    [InlineData("api:profile:*", Scope.TherapyReadWrite)]
    [InlineData("api:activity:*", Scope.SleepReadWrite)]
    public void FromPermissions_VerbWildcard_MapsToReadWrite(string permission, string expected)
    {
        // Nightscout's own roles are written with verb wildcards, so a subject migrated off one
        // holds these and nothing else.
        var scopes = ScopeTranslator.FromPermissions([permission]);

        Assert.Contains(expected, scopes);
        Assert.DoesNotContain(Scope.FullAccess, scopes);
    }

    [Fact]
    public void FromPermissions_NightscoutReadableWildcard_MapsToAllReadScopes()
    {
        // "*:*:read" is the permission on Nightscout's seeded "readable" role.
        var scopes = ScopeTranslator.FromPermissions(["*:*:read"]);

        Assert.Contains(Scope.GlucoseRead, scopes);
        Assert.Contains(Scope.TreatmentsRead, scopes);
        Assert.DoesNotContain(Scope.GlucoseReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_EntriesDelete_MapsToGlucoseReadWriteOnly()
    {
        // A delete verb on one collection is authority over that collection, not superuser: mapping
        // it to "*" handed a subject holding only "api:entries:delete" every other collection too.
        var scopes = ScopeTranslator.FromPermissions(["api:entries:delete"]);

        Assert.Contains(Scope.GlucoseReadWrite, scopes);
        Assert.DoesNotContain(Scope.FullAccess, scopes);
        Assert.DoesNotContain(Scope.TreatmentsReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_CareportalTreatmentsWildcard_CanDeleteTreatments()
    {
        // Nightscout's seeded careportal role is "api:treatments:*", which covers its delete verb.
        var scopes = ScopeTranslator.FromPermissions(["api:treatments:*"]);

        Assert.True(Scope.Satisfies(scopes, Scope.TreatmentsReadWrite));
    }

    [Fact]
    public void ToPermissions_ReadWriteScope_RoundTripsThroughItsDeleteVerb()
    {
        var permissions = ScopeTranslator.ToPermissions([Scope.TreatmentsReadWrite]);

        Assert.Contains("api:treatments:delete", permissions);
        Assert.Contains(
            Scope.TreatmentsReadWrite,
            ScopeTranslator.FromPermissions(permissions));
    }

    [Fact]
    public void FromPermissions_WildcardRead_MapsToAllReadScopes()
    {
        var permissions = new[] { "api:*:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.GlucoseRead, scopes);
        Assert.Contains(Scope.TreatmentsRead, scopes);
        Assert.Contains(Scope.DevicesRead, scopes);
        Assert.Contains(Scope.TherapyRead, scopes);
        Assert.Contains(Scope.AlertsRead, scopes);
        Assert.Contains(Scope.ReportsRead, scopes);
        Assert.Contains(Scope.IdentityRead, scopes);
        Assert.Contains(Scope.HeartRateRead, scopes);
        Assert.Contains(Scope.StepCountRead, scopes);
        Assert.Contains(Scope.SleepRead, scopes);
    }

    [Fact]
    public void FromPermissions_ReadableRole_MapsToAllReadScopes()
    {
        var permissions = new[] { "readable" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.GlucoseRead, scopes);
        Assert.Contains(Scope.TreatmentsRead, scopes);
        Assert.Contains(Scope.DevicesRead, scopes);
        Assert.Contains(Scope.TherapyRead, scopes);
        Assert.Contains(Scope.HeartRateRead, scopes);
        Assert.Contains(Scope.StepCountRead, scopes);
        Assert.Contains(Scope.SleepRead, scopes);
    }

    /// <summary>
    /// The legacy activity collection is Nocturne's merged read over heart rates, step counts, sleep
    /// sessions and StateSpans. Its read permission carries the three dedicated categories; the
    /// StateSpan half reads under treatments, which <c>api:treatments:read</c> grants separately.
    /// </summary>
    [Fact]
    public void FromPermissions_ActivityRead_MapsToTheDedicatedActivityCategories()
    {
        var scopes = ScopeTranslator.FromPermissions(["api:activity:read"]);

        Assert.Contains(Scope.HeartRateRead, scopes);
        Assert.Contains(Scope.StepCountRead, scopes);
        Assert.Contains(Scope.SleepRead, scopes);
        Assert.DoesNotContain(Scope.TreatmentsRead, scopes);
        Assert.DoesNotContain(Scope.GlucoseRead, scopes);
    }

    /// <summary>
    /// The V1/V2/V3 controllers are gated by the HasPermissions policy, which rejects an empty
    /// PermissionTrie before any scope check runs. A grant holding only a dedicated activity category
    /// must therefore still produce a legacy permission string, and not the activity collection's:
    /// that one reads back as all three categories.
    /// </summary>
    [Theory]
    [InlineData(Scope.HeartRateRead, "api:heartrate:read")]
    [InlineData(Scope.StepCountRead, "api:stepcount:read")]
    [InlineData(Scope.SleepRead, "api:sleep:read")]
    [InlineData(Scope.HeartRateReadWrite, "api:heartrate:read")]
    [InlineData(Scope.StepCountReadWrite, "api:stepcount:read")]
    [InlineData(Scope.SleepReadWrite, "api:sleep:read")]
    public void ToPermissions_OneActivityCategory_MapsToItsOwnResource(string scope, string expected)
    {
        var permissions = ScopeTranslator.ToPermissions([scope]);

        Assert.Contains(expected, permissions);
        Assert.DoesNotContain(permissions, permission => permission.StartsWith("api:activity:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToPermissions_EveryActivityRead_GrantsTheActivityCollectionRead()
    {
        var permissions = ScopeTranslator.ToPermissions(ActivityReads);

        Assert.Contains("api:activity:read", permissions);
        Assert.DoesNotContain("api:activity:create", permissions);
        Assert.Equal(Authority(ActivityReads), Authority(ScopeTranslator.FromPermissions(permissions)));
    }

    [Fact]
    public void ToPermissions_EveryActivityReadWrite_GrantsEveryVerbOnTheActivityCollection()
    {
        string[] scopes = [Scope.HeartRateReadWrite, Scope.StepCountReadWrite, Scope.SleepReadWrite];

        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:activity:read", permissions);
        Assert.Contains("api:activity:create", permissions);
        Assert.Contains("api:activity:update", permissions);
        Assert.Contains("api:activity:delete", permissions);
        Assert.Equal(Authority(scopes), Authority(ScopeTranslator.FromPermissions(permissions)));
    }

    [Theory]
    [InlineData("api:activity:create")]
    [InlineData("api:activity:update")]
    [InlineData("api:activity:delete")]
    public void FromPermissions_ActivityWriteVerb_MapsToReadWriteLikeEveryOtherCollection(string permission)
    {
        var scopes = ScopeTranslator.FromPermissions([permission]);

        Assert.Contains(Scope.HeartRateReadWrite, scopes);
        Assert.Contains(Scope.StepCountReadWrite, scopes);
        Assert.Contains(Scope.SleepReadWrite, scopes);
        Assert.DoesNotContain(Scope.TreatmentsReadWrite, scopes);
    }

    /// <summary>
    /// Sharing has no read tier, so a read verb on it must not reach the readwrite scope, and a
    /// read-only category has nothing for a write verb to reach.
    /// </summary>
    [Theory]
    [InlineData("api:sharing:read")]
    [InlineData("api:reports:create")]
    [InlineData("api:reports:*")]
    [InlineData("api:identity:update")]
    public void FromPermissions_VerbTheCategoryHasNoTierFor_GrantsNothing(string permission)
    {
        Assert.Empty(ScopeTranslator.FromPermissions([permission]));
    }

    [Fact]
    public void ToPermissions_AlertsReadWrite_CarriesNightscoutAck()
    {
        var permissions = ScopeTranslator.ToPermissions([Scope.AlertsReadWrite]);

        Assert.Contains("notifications:*:ack", permissions);
        Assert.DoesNotContain("notifications:*:ack", ScopeTranslator.ToPermissions([Scope.AlertsRead]));
    }

    /// <summary>
    /// See the remarks on <see cref="ScopeTranslator.ToPermissions"/>: a Shiro form here would put a
    /// device-only credential through the HasPermissions policy.
    /// </summary>
    [Fact]
    public void ToPermissions_MemberPersonalScopes_LeaveTheTrieEmpty()
    {
        Assert.Empty(ScopeTranslator.ToPermissions(Scope.MemberPersonalScopes));
    }

    [Fact]
    public void ToPermissions_FullAccessAmongOtherScopes_IsTheWildcardAlone()
    {
        var permissions = ScopeTranslator.ToPermissions([Scope.GlucoseRead, Scope.FullAccess]);

        Assert.Equal(["*"], permissions);
    }

    [Theory]
    [InlineData("notifications:*:ack")]
    [InlineData("*:*:ack")]
    public void FromPermissions_NightscoutAck_MapsToAlertsAcknowledgeOnly(string permission)
    {
        var scopes = ScopeTranslator.FromPermissions([permission]);

        Assert.Equal([Scope.AlertsAcknowledge], scopes);
    }

    [Fact]
    public void FromPermissions_NightscoutAck_GainsNoOtherAuthority()
    {
        var scopes = ScopeTranslator.FromPermissions(["notifications:*:ack"]);

        Assert.All(
            Scope.PermissionAtoms.Where(atom => atom != Scope.AlertsAcknowledge),
            atom => Assert.False(Scope.Satisfies(scopes, atom), $"notifications:*:ack must not grant {atom}"));
    }

    [Fact]
    public void ToPermissions_AlertsAcknowledge_RoundTripsThroughNightscoutAck()
    {
        var permissions = ScopeTranslator.ToPermissions([Scope.AlertsAcknowledge]);

        Assert.Equal(["notifications:*:ack"], permissions);
        Assert.Equal([Scope.AlertsAcknowledge], ScopeTranslator.FromPermissions(permissions));
    }

    [Fact]
    public void FromPermissions_MultiplePermissions_AggregatesScopes()
    {
        var permissions = new[] { "api:entries:read", "api:treatments:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.GlucoseRead, scopes);
        Assert.Contains(Scope.TreatmentsReadWrite, scopes);
        Assert.DoesNotContain(Scope.FullAccess, scopes);
    }

    [Fact]
    public void FromPermissions_UnknownPermission_IsIgnored()
    {
        var permissions = new[] { "api:unknown:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Empty(scopes);
    }

    [Fact]
    public void ToPermissions_EntriesRead_MapsBack()
    {
        var scopes = new[] { Scope.GlucoseRead };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:entries:read", permissions);
    }

    [Fact]
    public void ToPermissions_EntriesReadWrite_IncludesEveryWriteVerb()
    {
        var scopes = new[] { Scope.GlucoseReadWrite };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:entries:read", permissions);
        Assert.Contains("api:entries:create", permissions);
        Assert.Contains("api:entries:update", permissions);
        Assert.Contains("api:entries:delete", permissions);
    }

    [Fact]
    public void ToPermissions_FullAccess_MapsToWildcard()
    {
        var scopes = new[] { Scope.FullAccess };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("*", permissions);
        Assert.Single(permissions); // * covers everything, no need for individual permissions
    }

    [Fact]
    public void FromPermissions_FoodRead_MapsToFoodRead()
    {
        var permissions = new[] { "api:food:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.FoodRead, scopes);
        Assert.DoesNotContain(Scope.FoodReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_FoodCreate_MapsToFoodReadWrite()
    {
        var permissions = new[] { "api:food:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.FoodReadWrite, scopes);
    }

    [Fact]
    public void ToPermissions_FoodRead_MapsBack()
    {
        var scopes = new[] { Scope.FoodRead };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:food:read", permissions);
    }

    [Fact]
    public void ToPermissions_FoodReadWrite_IncludesEveryWriteVerb()
    {
        var scopes = new[] { Scope.FoodReadWrite };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:food:read", permissions);
        Assert.Contains("api:food:create", permissions);
        Assert.Contains("api:food:update", permissions);
        Assert.Contains("api:food:delete", permissions);
    }

    [Fact]
    public void FromPermissions_WildcardRead_IncludesFood()
    {
        var permissions = new[] { "api:*:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.FoodRead, scopes);
    }

    [Fact]
    public void FromPermissions_WildcardCreate_IncludesFood()
    {
        var permissions = new[] { "api:*:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(Scope.FoodReadWrite, scopes);
    }

    private static readonly string[] ActivityReads = [Scope.HeartRateRead, Scope.StepCountRead, Scope.SleepRead];

    /// <summary>
    /// The scopes <see cref="ScopeTranslator.ToPermissions"/> gives no Shiro form, for the reason in
    /// its remarks. They read back as nothing.
    /// </summary>
    private static readonly IReadOnlySet<string> NoShiroForm = Scope.MemberPersonalScopes;

    public static TheoryData<string> EveryScope() => [.. Scope.AllScopes];

    /// <summary>
    /// Clients read a grant's authority from the Shiro form, and the Nightscout subjects API writes
    /// that form back as the grant's scopes, so it must come back as the scope it was. A scope added
    /// without a Shiro form, or sharing one, fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryScope))]
    public void ToPermissions_RoundTripsToTheSameAuthority(string scope)
    {
        var roundTripped = ScopeTranslator.FromPermissions(ScopeTranslator.ToPermissions([scope]));

        string[] expected = NoShiroForm.Contains(scope) ? [] : [scope];
        Assert.Equal(Authority(expected), Authority(roundTripped));
    }

    public static TheoryData<string, string> EveryPairOfScopes()
    {
        var pairs = new TheoryData<string, string>();
        for (var i = 0; i < Scope.AllScopes.Count; i++)
        {
            for (var j = i + 1; j < Scope.AllScopes.Count; j++)
            {
                pairs.Add(Scope.AllScopes[i], Scope.AllScopes[j]);
            }
        }

        return pairs;
    }

    /// <summary>
    /// Two scopes together must not read back as more than the two, which is how a Shiro form
    /// spanning several scopes would fail while each scope alone round-trips.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryPairOfScopes))]
    public void ToPermissions_RoundTripsAPairToTheSameAuthority(string first, string second)
    {
        var roundTripped = ScopeTranslator.FromPermissions(ScopeTranslator.ToPermissions([first, second]));

        string[] expected = [.. new[] { first, second }.Where(scope => !NoShiroForm.Contains(scope))];
        Assert.Equal(Authority(expected), Authority(roundTripped));
    }

    [Fact]
    public void ToPermissions_RoundTripsEveryScopeTogetherToTheSameAuthority()
    {
        var roundTripped = ScopeTranslator.FromPermissions(ScopeTranslator.ToPermissions(Scope.AllScopes));

        Assert.Equal(Authority(Scope.AllScopes.Except(NoShiroForm)), Authority(roundTripped));
    }

    private static string[] Authority(IEnumerable<string> scopes)
    {
        var granted = scopes.ToList();
        return [.. Scope.AllScopes.Where(atom => Scope.Satisfies(granted, atom)).Order(StringComparer.Ordinal)];
    }
}
