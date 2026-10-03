using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.Connectors.GoogleHealth.Services;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class DevOnlyGoogleHealthMockTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Production_requires_both_explicit_opt_ins(bool devOnly, bool mock, bool redirected)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(Environments.Production);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DevOnlyEndpoints.EnableVariable] = devOnly.ToString(),
            [DevOnlyGoogleHealthMock.EnableVariable] = mock.ToString(),
        }).Build();
        var services = new ServiceCollection();
        services.AddDevOnlyGoogleHealthMock(environment.Object, config);
        var destination = new CaptureHandler();
        services.AddHttpClient<GoogleHealthAuthTokenProvider>().ConfigurePrimaryHttpMessageHandler(() => destination);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GoogleHealthAuthTokenProvider));
        using var response = await client.GetAsync("https://openidconnect.googleapis.com/v1/userinfo?test=value");
        destination.Uri!.AbsoluteUri.Should().Be(redirected
            ? "http://mocks:8080/googlehealth/openidconnect.googleapis.com/v1/userinfo?test=value"
            : "https://openidconnect.googleapis.com/v1/userinfo?test=value");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
