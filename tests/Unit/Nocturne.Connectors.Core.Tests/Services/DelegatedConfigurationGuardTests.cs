using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class DelegatedConfigurationGuardTests
{
    private class GuardedConfig : BaseConnectorConfiguration
    {
        [ConnectorProperty(ConnectorPropertyKey.Url, Format = "uri")]
        public string Url { get; set; } = string.Empty;

        [ConnectorProperty(ConnectorPropertyKey.Server, AllowedValues = ["US", "EU"])]
        public string Server { get; set; } = "US";

        [ConnectorProperty(ConnectorPropertyKey.ApiSecret, Secret = true)]
        public string ApiSecret { get; set; } = string.Empty;

        [ConnectorProperty(ConnectorPropertyKey.WriteBackEnabled, SendsRecords = true)]
        public bool WriteBackEnabled { get; set; }
    }

    private static IReadOnlyList<string> Refused(string? stored, string proposed, bool holdsSecret) =>
        DelegatedConfigurationGuard.RefusedKeys(
            new GuardedConfig(),
            stored is null ? null : JsonDocument.Parse(stored),
            JsonDocument.Parse(proposed),
            holdsSecret);

    [Fact]
    public void AChangedUrl_IsRefusedOnlyWhileASecretIsHeld()
    {
        Refused("""{"url":"https://a.example"}""", """{"url":"https://b.example"}""", holdsSecret: true)
            .Should().Equal("url");
        Refused("""{"url":"https://a.example"}""", """{"url":"https://b.example"}""", holdsSecret: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void AnUnchangedUrl_AndARegionChoice_AreNotRefused()
    {
        Refused("""{"url":"https://a.example","server":"US"}""", """{"url":"https://a.example","server":"EU"}""", true)
            .Should().BeEmpty();
    }

    [Fact]
    public void ADroppedUrl_IsAChange()
    {
        Refused("""{"url":"https://a.example"}""", """{"server":"EU"}""", holdsSecret: true).Should().Equal("url");
    }

    [Theory]
    [InlineData("""{"url":"https://a.example","writeBackEnabled":true}""", """{"url":"https://b.example","writeBackEnabled":true}""")]
    [InlineData("""{"url":"https://a.example","writeBackEnabled":true}""", """{"url":"https://b.example","writeBackEnabled":false}""")]
    [InlineData("""{"url":"https://a.example"}""", """{"url":"https://b.example","writeBackEnabled":true}""")]
    public void AChangedUrl_IsRefusedWhileRecordsAreSentBeforeOrAfter_WithNoSecretHeld(string stored, string proposed)
    {
        Refused(stored, proposed, holdsSecret: false).Should().Contain("url");
    }

    [Fact]
    public void AChangedUrl_IsRefusedWhileTheBaselineHoldsASecret()
    {
        DelegatedConfigurationGuard.RefusedKeys(
                new GuardedConfig { ApiSecret = "instance" },
                JsonDocument.Parse("""{"url":"https://a.example"}"""),
                JsonDocument.Parse("""{"url":"https://b.example"}"""),
                holdsStoredSecret: false)
            .Should().Equal("url");
    }

    [Fact]
    public void ABaselineThatSendsRecords_GuardsTheUrlAndIsNotChanged()
    {
        var baseline = new GuardedConfig { WriteBackEnabled = true };

        DelegatedConfigurationGuard.RefusedKeys(
                baseline,
                JsonDocument.Parse("""{"url":"https://a.example"}"""),
                JsonDocument.Parse("""{"url":"https://b.example"}"""),
                holdsStoredSecret: false)
            .Should().Equal("url");
        baseline.Url.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, """{"writeBackEnabled":true}""", true)]
    [InlineData("""{"writeBackEnabled":false}""", """{"writeBackEnabled":true}""", true)]
    [InlineData("""{"writeBackEnabled":true}""", """{"writeBackEnabled":true}""", false)]
    [InlineData("""{"writeBackEnabled":true}""", """{"writeBackEnabled":false}""", false)]
    [InlineData(null, """{"writeBackEnabled":false}""", false)]
    public void TurningOnAPropertyThatSendsRecords_IsRefusedWhetherOrNotASecretIsHeld(
        string? stored, string proposed, bool refused)
    {
        Refused(stored, proposed, holdsSecret: false).Contains("writeBackEnabled").Should().Be(refused);
    }

    private static IReadOnlyList<string> Locked(string? stored, bool holdsSecret) =>
        DelegatedConfigurationGuard.LockedKeys(
            new GuardedConfig(), stored is null ? null : JsonDocument.Parse(stored), holdsSecret);

    [Theory]
    [InlineData("""{"url":"https://a.example"}""", true, new[] { "url", "writeBackEnabled" })]
    [InlineData("""{"url":"https://a.example","writeBackEnabled":true}""", false, new[] { "url" })]
    [InlineData("""{"url":"https://a.example"}""", false, new[] { "writeBackEnabled" })]
    [InlineData(null, false, new[] { "writeBackEnabled" })]
    public void LockedKeys_AreTheDestinationWhileItCarriesSomethingAndEveryRecordSinkThatIsOff(
        string? stored, bool holdsSecret, string[] locked)
    {
        Locked(stored, holdsSecret).Should().BeEquivalentTo(locked);
    }

    [Theory]
    [InlineData("""{"url":"https://a.example"}""", true)]
    [InlineData("""{"url":"https://a.example","writeBackEnabled":true}""", false)]
    [InlineData("""{"url":"https://a.example"}""", false)]
    public void ChangingAnyLockedKey_IsRefused(string stored, bool holdsSecret)
    {
        var changed = new Dictionary<string, string>
        {
            ["url"] = """{"url":"https://b.example"}""",
            ["writeBackEnabled"] = """{"url":"https://a.example","writeBackEnabled":true}""",
        };

        foreach (var key in Locked(stored, holdsSecret))
            Refused(stored, changed[key], holdsSecret).Should().Contain(key);
    }
}
