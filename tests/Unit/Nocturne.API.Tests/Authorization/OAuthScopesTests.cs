using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class OAuthScopesTests
{
    [Theory]
    [InlineData("entries.read", true)]
    [InlineData("entries.readwrite", true)]
    [InlineData("treatments.read", true)]
    [InlineData("treatments.readwrite", true)]
    [InlineData("devicestatus.read", true)]
    [InlineData("devicestatus.readwrite", true)]
    [InlineData("profile.read", true)]
    [InlineData("profile.readwrite", true)]
    [InlineData("notifications.read", true)]
    [InlineData("notifications.readwrite", true)]
    [InlineData("reports.read", true)]
    [InlineData("identity.read", true)]
    [InlineData("sharing.readwrite", true)]
    [InlineData("*", true)]
    [InlineData("health.read", true)]
    [InlineData("invalid.scope", false)]
    [InlineData("entries.delete", false)]
    [InlineData("", false)]
    public void IsValid_ReturnsExpected(string scope, bool expected)
    {
        Assert.Equal(expected, OAuthScopes.IsValid(scope));
    }

    [Fact]
    public void Normalize_FullAccess_ExpandsToAllScopes()
    {
        var result = OAuthScopes.Normalize(new[] { "*" });

        Assert.Contains(OAuthScopes.FullAccess, result);
        Assert.Contains(OAuthScopes.EntriesRead, result);
        Assert.Contains(OAuthScopes.TreatmentsReadWrite, result);
        Assert.Contains(OAuthScopes.ProfileRead, result);
        Assert.Contains(OAuthScopes.DeviceStatusRead, result);
    }

    [Fact]
    public void Normalize_HealthRead_ExpandsToHealthScopes()
    {
        var result = OAuthScopes.Normalize(new[] { "health.read" });

        Assert.Contains(OAuthScopes.EntriesRead, result);
        Assert.Contains(OAuthScopes.TreatmentsRead, result);
        Assert.Contains(OAuthScopes.DeviceStatusRead, result);
        Assert.Contains(OAuthScopes.ProfileRead, result);
        Assert.DoesNotContain(OAuthScopes.NotificationsRead, result);
        Assert.DoesNotContain(OAuthScopes.FullAccess, result);
    }

    [Fact]
    public void Normalize_InvalidScopesAreIgnored()
    {
        var result = OAuthScopes.Normalize(new[] { "entries.read", "invalid.scope" });

        Assert.Contains(OAuthScopes.EntriesRead, result);
        Assert.Single(result);
    }

    [Fact]
    public void SatisfiesScope_ExactMatch()
    {
        var granted = new HashSet<string> { "entries.read" };
        Assert.True(OAuthScopes.SatisfiesScope(granted, "entries.read"));
    }

    [Fact]
    public void SatisfiesScope_FullAccessSatisfiesEverything()
    {
        var granted = new HashSet<string> { "*" };

        Assert.True(OAuthScopes.SatisfiesScope(granted, "entries.read"));
        Assert.True(OAuthScopes.SatisfiesScope(granted, "treatments.readwrite"));
        Assert.True(OAuthScopes.SatisfiesScope(granted, "profile.read"));
        Assert.True(OAuthScopes.SatisfiesScope(granted, "*"));
    }

    [Fact]
    public void SatisfiesScope_ReadWriteImpliesRead()
    {
        var granted = new HashSet<string> { "entries.readwrite" };

        Assert.True(OAuthScopes.SatisfiesScope(granted, "entries.read"));
        Assert.True(OAuthScopes.SatisfiesScope(granted, "entries.readwrite"));
        Assert.False(OAuthScopes.SatisfiesScope(granted, "treatments.read"));
    }

    [Fact]
    public void SatisfiesScope_ReadDoesNotImplyReadWrite()
    {
        var granted = new HashSet<string> { "entries.read" };

        Assert.True(OAuthScopes.SatisfiesScope(granted, "entries.read"));
        Assert.False(OAuthScopes.SatisfiesScope(granted, "entries.readwrite"));
    }

    [Fact]
    public void SatisfiesScope_NoScopesSatisfiesNothing()
    {
        var granted = new HashSet<string>();

        Assert.False(OAuthScopes.SatisfiesScope(granted, "entries.read"));
        Assert.False(OAuthScopes.SatisfiesScope(granted, "*"));
    }
}
