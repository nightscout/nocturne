using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class KnownOAuthClientsTests
{
    [Theory]
    [InlineData("xdrip-pixel9", "xDrip+")]
    [InlineData("xdrip-anything", "xDrip+")]
    [InlineData("aaps-samsung", "AAPS")]
    [InlineData("loop-iphone15", "Loop")]
    [InlineData("nightscout-web", "Nightscout")]
    [InlineData("sugarmate-v2", "Sugarmate")]
    [InlineData("nightwatch-1", "Nightwatch")]
    public void Match_KnownClient_ReturnsEntry(string clientId, string expectedDisplayName)
    {
        var entry = KnownOAuthClients.Match(clientId);

        Assert.NotNull(entry);
        Assert.Equal(expectedDisplayName, entry.DisplayName);
    }

    [Theory]
    [InlineData("unknown-app")]
    [InlineData("com.example.myapp")]
    [InlineData("")]
    public void Match_UnknownClient_ReturnsNull(string clientId)
    {
        var entry = KnownOAuthClients.Match(clientId);

        Assert.Null(entry);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var entry = KnownOAuthClients.Match("XDRIP-pixel9");

        Assert.NotNull(entry);
        Assert.Equal("xDrip+", entry.DisplayName);
    }

    [Fact]
    public void AllEntries_HaveRequiredFields()
    {
        foreach (var entry in KnownOAuthClients.Entries)
        {
            Assert.False(string.IsNullOrEmpty(entry.ClientIdPattern));
            Assert.False(string.IsNullOrEmpty(entry.DisplayName));
            Assert.NotEmpty(entry.TypicalScopes);

            // All typical scopes should be valid
            foreach (var scope in entry.TypicalScopes)
            {
                Assert.True(OAuthScopes.IsValid(scope), $"Invalid scope '{scope}' in {entry.DisplayName}");
            }
        }
    }
}
