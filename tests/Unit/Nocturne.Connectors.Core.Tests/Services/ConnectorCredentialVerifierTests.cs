using System.Text.Json;
using FluentAssertions;
using Moq;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// The shared verification flow, over a connector whose destination is a member-supplied URL.
/// </summary>
public class ConnectorCredentialVerifierTests
{
    private const string StoredUrl = "https://stored.example.com";
    private const string StoredSecret = "stored-api-secret";

    [Fact]
    public async Task VerifyAsync_StartsFromTheRegistrationDefaults()
    {
        var verifier = new RecordingVerifier(new UrlConfig { Url = "https://operator.example.com" });

        await verifier.VerifyAsync(null, new() { ["apiSecret"] = "submitted" }, [], CancellationToken.None);

        verifier.Verified.Should().ContainSingle().Which.Url.Should().Be("https://operator.example.com");
    }

    [Fact]
    public async Task VerifyAsync_StoredSecret_IsSentToTheStoredUrl()
    {
        var verifier = new RecordingVerifier(new UrlConfig(), Stored());

        await verifier.VerifyAsync(null, [], ["apiSecret"], CancellationToken.None);

        var config = verifier.Verified.Should().ContainSingle().Subject;
        config.Url.Should().Be(StoredUrl);
        config.ApiSecret.Should().Be(StoredSecret);
    }

    [Fact]
    public async Task VerifyAsync_StoredSecretWithASubmittedUrl_IsRefusedBeforeReadingSecrets()
    {
        var configService = Stored();
        var verifier = new RecordingVerifier(new UrlConfig(), configService);

        var verify = () => verifier.VerifyAsync(
            JsonDocument.Parse("""{"url":"https://attacker.example.com"}"""), [], ["apiSecret"],
            CancellationToken.None);

        await verify.Should().ThrowAsync<InvalidVerificationRequestException>();
        verifier.Verified.Should().BeEmpty();
        configService.Verify(s => s.GetSecretsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_StoredSecretWithTheStoredUrlRepeated_IsAllowed()
    {
        var verifier = new RecordingVerifier(new UrlConfig(), Stored());

        await verifier.VerifyAsync(
            JsonDocument.Parse($$"""{"url":"{{StoredUrl}}"}"""), [], ["apiSecret"], CancellationToken.None);

        verifier.Verified.Should().ContainSingle().Which.ApiSecret.Should().Be(StoredSecret);
    }

    [Fact]
    public async Task VerifyAsync_StoredSecrets_UsesOnlyTheNamedOnes()
    {
        var configService = Stored(new Dictionary<string, string>
        {
            ["apiSecret"] = StoredSecret,
            ["accessToken"] = "stored-token",
        });
        var verifier = new RecordingVerifier(new UrlConfig(), configService);

        await verifier.VerifyAsync(null, [], ["apiSecret"], CancellationToken.None);

        verifier.Verified.Should().ContainSingle().Which.Token.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_DefaultsCarryASecret_TheSecretIsNotUsed()
    {
        var verifier = new RecordingVerifier(new UrlConfig { ApiSecret = "operator-api-secret" });

        var result = await verifier.VerifyAsync(
            JsonDocument.Parse("""{"url":"https://attacker.example.com"}"""), [], [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().StartWith("Missing required fields");
        verifier.Verified.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_StoredSecret_DoesNotTakeOtherSecretsFromTheDefaults()
    {
        var verifier = new RecordingVerifier(new UrlConfig { Token = "operator-token" }, Stored());

        await verifier.VerifyAsync(null, [], ["apiSecret"], CancellationToken.None);

        verifier.Verified.Should().ContainSingle().Which.Token.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_StoredSecretCannotBeRead_FailsTheVerification()
    {
        var configService = Stored();
        configService.Setup(s => s.GetSecretsAsync("UrlConnector", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("decryption failed"));
        var verifier = new RecordingVerifier(new UrlConfig(), configService);

        var result = await verifier.VerifyAsync(null, [], ["apiSecret"], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().NotContain("decryption");
        verifier.Verified.Should().BeEmpty();
    }

    private static Mock<IConnectorConfigurationService> Stored(Dictionary<string, string>? secrets = null)
    {
        var configService = new Mock<IConnectorConfigurationService>(MockBehavior.Strict);
        configService.Setup(s => s.GetConfigurationAsync("UrlConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "UrlConnector",
                Configuration = JsonDocument.Parse($$"""{"url":"{{StoredUrl}}"}"""),
            });
        configService.Setup(s => s.GetSecretsAsync("UrlConnector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets ?? new Dictionary<string, string> { ["apiSecret"] = StoredSecret });
        return configService;
    }

    private sealed class UrlConfig : BaseConnectorConfiguration
    {
        [ConnectorProperty(ConnectorPropertyKey.Url, Required = true)]
        public string Url { get; set; } = string.Empty;

        [ConnectorProperty(ConnectorPropertyKey.ApiSecret, Required = true, Secret = true)]
        public string ApiSecret { get; set; } = string.Empty;

        [ConnectorProperty(ConnectorPropertyKey.AccessToken, Secret = true)]
        public string Token { get; set; } = string.Empty;
    }

    private sealed class RecordingVerifier(UrlConfig defaults, Mock<IConnectorConfigurationService>? configService = null)
        : ConnectorCredentialVerifier<UrlConfig>(
            new ConnectorRegistration<UrlConfig>(defaults, "UrlConnector"),
            (configService ?? new Mock<IConnectorConfigurationService>(MockBehavior.Strict)).Object)
    {
        public List<UrlConfig> Verified { get; } = [];

        public override string ConnectorId => "urlconnector";

        protected override Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
            UrlConfig config, CancellationToken ct)
        {
            Verified.Add(config);
            return Task.FromResult(ConnectorCredentialVerificationResult.Verified());
        }
    }
}
