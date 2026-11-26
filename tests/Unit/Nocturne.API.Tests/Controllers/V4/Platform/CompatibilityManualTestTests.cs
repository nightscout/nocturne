using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Compatibility;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The manual compatibility test names the Nightscout side by its origin in every log line, and the
/// configured URL is never shown with its token.
/// </summary>
[Trait("Category", "Unit")]
public class CompatibilityManualTestTests
{
    /// <summary>Nothing listens on the discard port, so both sides fail at once without leaving the machine.</summary>
    private const string Unanswered = "127.0.0.1:9";

    private sealed class RecordingLogger : ILogger<CompatibilityController>
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = [];

        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
                Lines.Add(exception.ToString());
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                States.Add(values);
        }

        public object? Value(string name) =>
            States.SelectMany(s => s).FirstOrDefault(kv => kv.Key == name).Value;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static CompatibilityController Controller(
        RecordingLogger logger,
        NightscoutConnectorConfiguration? nightscoutConfig = null,
        RecordingHandler? nightscout = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(CompatibilityProxyServiceExtensions.ManualTestHttpClientName))
            .Returns(() => new HttpClient(nightscout ?? new RecordingHandler(), disposeHandler: false));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString(Unanswered);

        return new CompatibilityController(
            Mock.Of<IDiscrepancyPersistenceService>(),
            Mock.Of<IDiscrepancyAnalysisRepository>(),
            factory.Object,
            Options.Create(new CompatibilityProxyConfiguration()),
            logger,
            nightscoutConfig)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Theory]
    [InlineData($"http://{Unanswered}?token=abc", "/api/v1/status", $"http://{Unanswered}/api/v1/status?token=abc")]
    [InlineData($"http://{Unanswered}/nightscout?token=abc", "/api/v1/status", $"http://{Unanswered}/nightscout/api/v1/status?token=abc")]
    [InlineData($"http://{Unanswered}/nightscout/?token=abc", "api/v1/entries.json?count=1", $"http://{Unanswered}/nightscout/api/v1/entries.json?count=1&token=abc")]
    [InlineData($"http://{Unanswered}/nightscout", "/api/v1/status", $"http://{Unanswered}/nightscout/api/v1/status")]
    public async Task The_nightscout_side_is_read_under_the_configured_path_with_its_query(
        string nightscoutUrl, string queryPath, string expected)
    {
        var nightscout = new RecordingHandler();

        var response = await Controller(new RecordingLogger(), nightscout: nightscout).TestApiComparison(new ManualTestRequest
        {
            NightscoutUrl = nightscoutUrl,
            QueryPath = queryPath,
        });

        response.Result.Should().BeOfType<OkObjectResult>();
        nightscout.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData($"http://{Unanswered}/nightscout?token=synthetic-token", "/api/v1/status")]
    [InlineData($"http://{Unanswered}/AGYLdvgzOeq/?token=synthetic-token&key=k", "api/v1/entries.json?count=1")]
    public async Task No_log_line_carries_the_nightscout_urls_path_or_query(string nightscoutUrl, string queryPath)
    {
        var logger = new RecordingLogger();

        var response = await Controller(logger).TestApiComparison(new ManualTestRequest
        {
            NightscoutUrl = nightscoutUrl,
            QueryPath = queryPath,
        });

        response.Result.Should().BeOfType<OkObjectResult>();
        logger.Value("NightscoutOrigin").Should().Be($"http://{Unanswered}");
        logger.Lines.Should().NotBeEmpty().And.OnlyContain(line =>
            !line.Contains("synthetic-token") && !line.Contains("AGYLdvgzOeq") && !line.Contains("nightscout/"));
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
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(NightscoutAddress.InvalidUrlMessage);
    }

    [Theory]
    [InlineData("https://user:pass@ns.example/nightscout/?token=synthetic-token", "ns.example/…")]
    [InlineData("https://ns.example/AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI/", "ns.example/…")]
    [InlineData("https://ns.example:8443/?token=synthetic-token", "ns.example:8443")]
    [InlineData("", "")]
    public void The_configuration_shows_the_nightscout_url_as_its_host_with_no_path(string configured, string expected)
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
        var nightscout = new RecordingHandler();

        var response = await Controller(logger, nightscout: nightscout).TestApiComparison(new ManualTestRequest
        {
            NightscoutUrl = $"http://{Unanswered}/nightscout",
            QueryPath = queryPath,
        });

        var problem = response.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(NightscoutAddress.OutsideBaseMessage);
        logger.States.Should().BeEmpty("nothing is fetched");
        nightscout.Requests.Should().BeEmpty();
    }
}
