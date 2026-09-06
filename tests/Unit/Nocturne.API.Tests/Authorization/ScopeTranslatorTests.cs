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
    /// must therefore still produce a legacy permission string.
    /// </summary>
    [Theory]
    [InlineData(Scope.HeartRateRead)]
    [InlineData(Scope.StepCountRead)]
    [InlineData(Scope.SleepRead)]
    [InlineData(Scope.HeartRateReadWrite)]
    [InlineData(Scope.StepCountReadWrite)]
    [InlineData(Scope.SleepReadWrite)]
    public void ToPermissions_ActivityCategories_MapBackToTheActivityCollection(string scope)
    {
        var permissions = ScopeTranslator.ToPermissions([scope]);

        Assert.Contains("api:activity:read", permissions);
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
}
