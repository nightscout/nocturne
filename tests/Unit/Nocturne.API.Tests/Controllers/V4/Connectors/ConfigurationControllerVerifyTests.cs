using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// Covers the credential verification endpoint: dispatch to the connector's registered verifier,
/// the "not supported" outcome for connectors without one, that nothing is ever persisted, and
/// that the endpoint stays behind the controller's authentication gate.
/// </summary>
public class ConfigurationControllerVerifyTests
{
    private readonly Mock<IConnectorConfigurationService> _configService = new(MockBehavior.Strict);

    private ConfigurationController BuildController() =>
        new(_configService.Object, NullLogger<ConfigurationController>.Instance);

    private static VerifyConnectorCredentialsRequest BuildRequest() => new()
    {
        Configuration = JsonDocument.Parse("""{"email":"user@example.com"}"""),
        Secrets = new Dictionary<string, string> { ["password"] = "secret" },
    };

    [Fact]
    public async Task VerifyCredentials_NoVerifierRegistered_ReportsNotSupported()
    {
        var controller = BuildController();

        var result = await controller.VerifyCredentials(
            "Dexcom", BuildRequest(), [], CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var outcome = Assert.IsType<ConnectorCredentialVerificationResult>(okResult.Value);
        Assert.False(outcome.Supported);
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Message);
    }

    [Fact]
    public async Task VerifyCredentials_DispatchesToVerifierCaseInsensitively()
    {
        var verifier = new Mock<IConnectorCredentialVerifier>();
        verifier.SetupGet(v => v.ConnectorId).Returns("glooko");
        verifier.Setup(v => v.VerifyAsync(
                It.IsAny<JsonDocument?>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorCredentialVerificationResult.Verified());

        var controller = BuildController();

        var result = await controller.VerifyCredentials(
            "Glooko", BuildRequest(), [verifier.Object], CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var outcome = Assert.IsType<ConnectorCredentialVerificationResult>(okResult.Value);
        Assert.True(outcome.Supported);
        Assert.True(outcome.Success);
        verifier.Verify(v => v.VerifyAsync(
            It.IsAny<JsonDocument?>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyCredentials_NeverPersistsTheSubmittedValues()
    {
        var verifier = new Mock<IConnectorCredentialVerifier>();
        verifier.SetupGet(v => v.ConnectorId).Returns("glooko");
        verifier.Setup(v => v.VerifyAsync(
                It.IsAny<JsonDocument?>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorCredentialVerificationResult.Verified());

        var controller = BuildController();

        await controller.VerifyCredentials(
            "glooko", BuildRequest(), [verifier.Object], CancellationToken.None);

        // Strict mock: any call to the configuration service (save, secrets, anything) would throw.
        _configService.VerifyNoOtherCalls();
    }

    [Fact]
    public void VerifyCredentials_IsNotAllowAnonymous()
    {
        var method = typeof(ConfigurationController)
            .GetMethod(nameof(ConfigurationController.VerifyCredentials));

        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(ConfigurationController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void VerifyCredentials_RequiresTenantSettingsScopeAndIsRateLimited()
    {
        var method = typeof(ConfigurationController)
            .GetMethod(nameof(ConfigurationController.VerifyCredentials));

        Assert.NotNull(method);
        var scope = method!.GetCustomAttribute<Nocturne.API.Attributes.RequireScopeAttribute>();
        Assert.NotNull(scope);
        Assert.Contains(
            Nocturne.Core.Models.Authorization.TenantPermissions.TenantSettings,
            scope!.RequiredScopes);
        var rateLimit = method.GetCustomAttribute<
            Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimit);
        Assert.Equal("connector-verify", rateLimit!.PolicyName);
    }
}
