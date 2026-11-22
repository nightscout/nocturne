using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// Saving a Nightscout API secret adopts it as a working credential, so only the account holder's
/// own sign-in may; every other connector credential stays savable by any credential holding
/// <c>tenant.settings</c>, platform access and the instance key included.
/// </summary>
public class ConfigurationControllerSaveSecretsTests
{
    private readonly Mock<IConnectorConfigurationService> _configService = new();

    private readonly Guid _subjectId = Guid.CreateVersion7();

    private ConfigurationController Controller(AuthType authType)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = _subjectId,
        };

        return new ConfigurationController(_configService.Object, NullLogger<ConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.InstanceKey)]
    [InlineData(AuthType.PlatformAccess)]
    public async Task NightscoutApiSecret_FromADelegatedCredential_IsForbiddenAndNotSaved(AuthType authType)
    {
        var result = await Controller(authType).SaveSecrets(
            "nightscout", new Dictionary<string, string> { ["apiSecret"] = "s" }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        _configService.Verify(s => s.SaveSecretsAsync(
            It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CredentialMaker?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NightscoutApiSecret_FromASession_IsSaved()
    {
        var result = await Controller(AuthType.SessionCookie).SaveSecrets(
            "Nightscout", new Dictionary<string, string> { ["ApiSecret"] = "s" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _configService.Verify(s => s.SaveSecretsAsync(
            "Nightscout", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(),
            It.Is<CredentialMaker?>(maker => maker != null && maker.SubjectId == _subjectId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(AuthType.InstanceKey)]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.ApiKey)]
    public async Task OtherConnectorCredentials_FromAnyCredential_AreSaved(AuthType authType)
    {
        var result = await Controller(authType).SaveSecrets(
            "dexcom", new Dictionary<string, string> { ["password"] = "s" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
