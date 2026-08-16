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

        Assert.Contains(OAuthScopes.FullAccess, scopes);
        // Full access should also include all individual scopes
        Assert.Contains(OAuthScopes.GlucoseRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_AdminRole_GrantsFullAccess()
    {
        var permissions = new[] { "admin" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.FullAccess, scopes);
    }

    [Fact]
    public void FromPermissions_EntriesRead_MapsToEntriesRead()
    {
        var permissions = new[] { "api:entries:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.GlucoseRead, scopes);
        Assert.DoesNotContain(OAuthScopes.GlucoseReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_EntriesCreate_MapsToEntriesReadWrite()
    {
        var permissions = new[] { "api:entries:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.GlucoseReadWrite, scopes);
    }

    [Theory]
    [InlineData("api:entries:*", OAuthScopes.GlucoseReadWrite)]
    [InlineData("api:treatments:*", OAuthScopes.TreatmentsReadWrite)]
    [InlineData("api:devicestatus:*", OAuthScopes.DevicesReadWrite)]
    [InlineData("api:food:*", OAuthScopes.FoodReadWrite)]
    [InlineData("api:profile:*", OAuthScopes.TherapyReadWrite)]
    [InlineData("api:activity:*", OAuthScopes.SleepReadWrite)]
    public void FromPermissions_VerbWildcard_MapsToReadWrite(string permission, string expected)
    {
        // Nightscout's own roles are written with verb wildcards, so a subject migrated off one
        // holds these and nothing else.
        var scopes = ScopeTranslator.FromPermissions([permission]);

        Assert.Contains(expected, scopes);
        Assert.DoesNotContain(OAuthScopes.FullAccess, scopes);
    }

    [Fact]
    public void FromPermissions_NightscoutReadableWildcard_MapsToAllReadScopes()
    {
        // "*:*:read" is the permission on Nightscout's seeded "readable" role.
        var scopes = ScopeTranslator.FromPermissions(["*:*:read"]);

        Assert.Contains(OAuthScopes.GlucoseRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsRead, scopes);
        Assert.DoesNotContain(OAuthScopes.GlucoseReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_EntriesDelete_MapsToFullAccess()
    {
        var permissions = new[] { "api:entries:delete" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.FullAccess, scopes);
    }

    [Fact]
    public void FromPermissions_WildcardRead_MapsToAllReadScopes()
    {
        var permissions = new[] { "api:*:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.GlucoseRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsRead, scopes);
        Assert.Contains(OAuthScopes.DevicesRead, scopes);
        Assert.Contains(OAuthScopes.TherapyRead, scopes);
        Assert.Contains(OAuthScopes.AlertsRead, scopes);
        Assert.Contains(OAuthScopes.ReportsRead, scopes);
        Assert.Contains(OAuthScopes.IdentityRead, scopes);
        Assert.Contains(OAuthScopes.HeartRateRead, scopes);
        Assert.Contains(OAuthScopes.StepCountRead, scopes);
        Assert.Contains(OAuthScopes.SleepRead, scopes);
    }

    [Fact]
    public void FromPermissions_ReadableRole_MapsToAllReadScopes()
    {
        var permissions = new[] { "readable" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.GlucoseRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsRead, scopes);
        Assert.Contains(OAuthScopes.DevicesRead, scopes);
        Assert.Contains(OAuthScopes.TherapyRead, scopes);
        Assert.Contains(OAuthScopes.HeartRateRead, scopes);
        Assert.Contains(OAuthScopes.StepCountRead, scopes);
        Assert.Contains(OAuthScopes.SleepRead, scopes);
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

        Assert.Contains(OAuthScopes.HeartRateRead, scopes);
        Assert.Contains(OAuthScopes.StepCountRead, scopes);
        Assert.Contains(OAuthScopes.SleepRead, scopes);
        Assert.DoesNotContain(OAuthScopes.TreatmentsRead, scopes);
        Assert.DoesNotContain(OAuthScopes.GlucoseRead, scopes);
    }

    /// <summary>
    /// The V1/V2/V3 controllers are gated by the HasPermissions policy, which rejects an empty
    /// PermissionTrie before any scope check runs. A grant holding only a dedicated activity category
    /// must therefore still produce a legacy permission string.
    /// </summary>
    [Theory]
    [InlineData(OAuthScopes.HeartRateRead)]
    [InlineData(OAuthScopes.StepCountRead)]
    [InlineData(OAuthScopes.SleepRead)]
    [InlineData(OAuthScopes.HeartRateReadWrite)]
    [InlineData(OAuthScopes.StepCountReadWrite)]
    [InlineData(OAuthScopes.SleepReadWrite)]
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

        Assert.Contains(OAuthScopes.GlucoseRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsReadWrite, scopes);
        Assert.DoesNotContain(OAuthScopes.FullAccess, scopes);
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
        var scopes = new[] { OAuthScopes.GlucoseRead };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:entries:read", permissions);
    }

    [Fact]
    public void ToPermissions_EntriesReadWrite_IncludesReadCreateUpdate()
    {
        var scopes = new[] { OAuthScopes.GlucoseReadWrite };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:entries:read", permissions);
        Assert.Contains("api:entries:create", permissions);
        Assert.Contains("api:entries:update", permissions);
        Assert.DoesNotContain("api:entries:delete", permissions);
    }

    [Fact]
    public void ToPermissions_FullAccess_MapsToWildcard()
    {
        var scopes = new[] { OAuthScopes.FullAccess };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("*", permissions);
        Assert.Single(permissions); // * covers everything, no need for individual permissions
    }

    [Fact]
    public void FromPermissions_FoodRead_MapsToFoodRead()
    {
        var permissions = new[] { "api:food:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.FoodRead, scopes);
        Assert.DoesNotContain(OAuthScopes.FoodReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_FoodCreate_MapsToFoodReadWrite()
    {
        var permissions = new[] { "api:food:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.FoodReadWrite, scopes);
    }

    [Fact]
    public void ToPermissions_FoodRead_MapsBack()
    {
        var scopes = new[] { OAuthScopes.FoodRead };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:food:read", permissions);
    }

    [Fact]
    public void ToPermissions_FoodReadWrite_IncludesReadCreateUpdate()
    {
        var scopes = new[] { OAuthScopes.FoodReadWrite };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:food:read", permissions);
        Assert.Contains("api:food:create", permissions);
        Assert.Contains("api:food:update", permissions);
        Assert.DoesNotContain("api:food:delete", permissions);
    }

    [Fact]
    public void FromPermissions_WildcardRead_IncludesFood()
    {
        var permissions = new[] { "api:*:read" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.FoodRead, scopes);
    }

    [Fact]
    public void FromPermissions_WildcardCreate_IncludesFood()
    {
        var permissions = new[] { "api:*:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.FoodReadWrite, scopes);
    }
}
