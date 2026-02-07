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
        Assert.Contains(OAuthScopes.EntriesRead, scopes);
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

        Assert.Contains(OAuthScopes.EntriesRead, scopes);
        Assert.DoesNotContain(OAuthScopes.EntriesReadWrite, scopes);
    }

    [Fact]
    public void FromPermissions_EntriesCreate_MapsToEntriesReadWrite()
    {
        var permissions = new[] { "api:entries:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.EntriesReadWrite, scopes);
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

        Assert.Contains(OAuthScopes.EntriesRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsRead, scopes);
        Assert.Contains(OAuthScopes.DeviceStatusRead, scopes);
        Assert.Contains(OAuthScopes.ProfileRead, scopes);
        Assert.Contains(OAuthScopes.NotificationsRead, scopes);
        Assert.Contains(OAuthScopes.ReportsRead, scopes);
        Assert.Contains(OAuthScopes.IdentityRead, scopes);
    }

    [Fact]
    public void FromPermissions_ReadableRole_MapsToAllReadScopes()
    {
        var permissions = new[] { "readable" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.EntriesRead, scopes);
        Assert.Contains(OAuthScopes.TreatmentsRead, scopes);
        Assert.Contains(OAuthScopes.DeviceStatusRead, scopes);
        Assert.Contains(OAuthScopes.ProfileRead, scopes);
    }

    [Fact]
    public void FromPermissions_MultiplePermissions_AggregatesScopes()
    {
        var permissions = new[] { "api:entries:read", "api:treatments:create" };
        var scopes = ScopeTranslator.FromPermissions(permissions);

        Assert.Contains(OAuthScopes.EntriesRead, scopes);
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
        var scopes = new[] { OAuthScopes.EntriesRead };
        var permissions = ScopeTranslator.ToPermissions(scopes);

        Assert.Contains("api:entries:read", permissions);
    }

    [Fact]
    public void ToPermissions_EntriesReadWrite_IncludesReadCreateUpdate()
    {
        var scopes = new[] { OAuthScopes.EntriesReadWrite };
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
}
