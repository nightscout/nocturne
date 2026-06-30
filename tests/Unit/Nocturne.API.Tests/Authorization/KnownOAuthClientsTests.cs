using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class KnownOAuthClientsTests
{
    [Theory]
    [InlineData("org.nightscout.trio", "Trio")]
    [InlineData("com.eveningoutpost.dexdrip", "xDrip+")]
    [InlineData("info.nightscout.androidaps", "AAPS")]
    [InlineData("org.loopkit.loop", "Loop")]
    [InlineData("org.nightscout.cgm-remote-monitor", "Nightscout (server)")]
    [InlineData("com.tandemdiabetes.sugarmate", "Sugarmate")]
    [InlineData("se.cornixit.nightwatch", "Nightwatch")]
    [InlineData("com.nocturne.tray", "Nocturne Tray")]
    [InlineData("dev.nocturne.prelude", "Prelude")]
    public void MatchBySoftwareId_ReturnsEntry_ForKnownSoftwareId(string softwareId, string expectedDisplayName)
    {
        var entry = KnownOAuthClients.MatchBySoftwareId(softwareId);

        entry.Should().NotBeNull();
        entry!.DisplayName.Should().Be(expectedDisplayName);
    }

    [Theory]
    [InlineData("com.evilcorp.fake")]
    [InlineData("unknown")]
    [InlineData("")]
    public void MatchBySoftwareId_ReturnsNull_ForUnknown(string softwareId)
    {
        KnownOAuthClients.MatchBySoftwareId(softwareId).Should().BeNull();
    }

    [Fact]
    public void MatchBySoftwareId_IsCaseSensitive()
    {
        // software_id is case-sensitive per RFC 7591
        KnownOAuthClients.MatchBySoftwareId("ORG.NIGHTSCOUT.TRIO").Should().BeNull();
    }

    [Fact]
    public void AllEntries_HaveRequiredFields()
    {
        foreach (var entry in KnownOAuthClients.Entries)
        {
            entry.SoftwareId.Should().NotBeNullOrEmpty($"{entry.DisplayName} must have a SoftwareId");
            entry.DisplayName.Should().NotBeNullOrEmpty();
            entry.TypicalScopes.Should().NotBeEmpty($"{entry.DisplayName} must have typical scopes");

            foreach (var scope in entry.TypicalScopes)
            {
                OAuthScopes.IsValid(scope).Should().BeTrue($"'{scope}' in {entry.DisplayName} must be a valid scope");
            }
        }
    }

    [Fact]
    public void AllEntries_HaveUniqueSoftwareIds()
    {
        var softwareIds = KnownOAuthClients.Entries.Select(e => e.SoftwareId).ToList();
        softwareIds.Should().OnlyHaveUniqueItems();
    }
}
