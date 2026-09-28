using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Compatibility;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The manual compatibility test reads the Nightscout side under the URL's path, and never with
/// the URL's query spliced into that path; the configured URL is never shown with its token.
/// </summary>
[Trait("Category", "Unit")]
public class CompatibilityManualTestTests
{
    /// <summary>Nothing listens on the discard port, so both sides fail at once without leaving the machine.</summary>
    private const string Unanswered = "127.0.0.1:9";

    private sealed class RecordingLogger : ILogger<CompatibilityController>
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                States.Add(values);
        }

        public object? Value(string name) =>
            States.SelectMany(s => s).FirstOrDefault(kv => kv.Key == name).Value;
    }

    private static CompatibilityController Controller(
        RecordingLogger logger, NightscoutConnectorConfiguration? nightscoutConfig = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString(Unanswered);

        return new CompatibilityController(
            Mock.Of<IDiscrepancyPersistenceService>(),
            Mock.Of<IDiscrepancyAnalysisRepository>(),
            Options.Create(new CompatibilityProxyConfiguration()),
            logger,
            nightscoutConfig)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Theory]
    [InlineData($"http://{Unanswered}/nightscout?token=synthetic-token", "/api/v1/status", $"http://{Unanswered}/nightscout/api/v1/status")]
    [InlineData($"http://{Unanswered}/nightscout/", "api/v1/entries.json?count=1", $"http://{Unanswered}/nightscout/api/v1/entries.json?count=1")]
    [InlineData($"http://{Unanswered}", "/api/v1/status", $"http://{Unanswered}/api/v1/status")]
    public async Task The_nightscout_side_is_read_under_the_configured_path(
        string nightscoutUrl, string queryPath, string expected)
    {
        var logger = new RecordingLogger();

        var response = await Controller(logger).TestApiComparison(new ManualTestRequest
        {
            NightscoutUrl = nightscoutUrl,
            QueryPath = queryPath,
        });

        response.Result.Should().BeOfType<OkObjectResult>();
        logger.Value("NightscoutUrl").Should().BeOfType<Uri>().Which.AbsoluteUri.Should().Be(expected);
    }

    [Fact]
    public async Task An_address_that_is_not_http_is_refused()
    {
        var response = await Controller(new RecordingLogger()).TestApiComparison(new ManualTestRequest
        {
            NightscoutUrl = "ftp://ns.example/nightscout",
            QueryPath = "/api/v1/status",
        });

        var problem = response.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(NightscoutBaseUri.InvalidUrlMessage);
    }

    [Theory]
    [InlineData("https://user:pass@ns.example/nightscout/?token=synthetic-token", "https://ns.example/nightscout")]
    [InlineData("", "")]
    public void The_configuration_shows_the_nightscout_url_without_its_token(string configured, string expected)
    {
        var response = Controller(new RecordingLogger(), new NightscoutConnectorConfiguration { Url = configured })
            .GetConfiguration();

        response.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ProxyConfigurationDto>()
            .Which.NightscoutUrl.Should().Be(expected);
    }

    [Theory]
    [InlineData("/http://other.example/api/v1/status")]
    [InlineData("/../api/v1/status")]
    public async Task A_query_path_that_leaves_the_nightscout_url_is_refused(string queryPath)
    {
        var logger = new RecordingLogger();

        var response = await Controller(logger).TestApiComparison(new ManualTestRequest
        {
            NightscoutUrl = $"http://{Unanswered}/nightscout",
            QueryPath = queryPath,
        });

        var problem = response.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(NightscoutBaseUri.OutsideBaseMessage);
        logger.States.Should().BeEmpty("nothing is fetched");
    }
}
