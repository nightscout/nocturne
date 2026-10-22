using System.Collections.Concurrent;
using Activity = System.Diagnostics.Activity;
using ActivityKind = System.Diagnostics.ActivityKind;
using ActivitySource = System.Diagnostics.ActivitySource;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Nocturne.API.Tests.Configuration;

/// <summary>
/// A credential in a route segment must not reach a log record, a log scope or a span, since anyone
/// who can read telemetry could replay it.
/// </summary>
[Trait("Category", "Unit")]
public partial class CredentialPathRedactionTests : IClassFixture<CredentialPathRedactionTests.TelemetryCaptureFactory>
{
    private readonly TelemetryCaptureFactory _factory;
    private readonly HttpClient _client;

    public CredentialPathRedactionTests(TelemetryCaptureFactory factory)
    {
        _factory = factory;
        _factory.Services.GetRequiredService<TracerProvider>();
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("GET", "/api/v2/authorization/request/{0}", "/api/v2/authorization/request/{accessToken}")]
    [InlineData("GET", "/api/v4/member-invites/{0}/info", "/api/v4/member-invites/{token}/info")]
    [InlineData("POST", "/api/v4/member-invites/{0}/accept", "/api/v4/member-invites/{token}/accept")]
    [InlineData("GET", "/api/v4/alert-invites/{0}", "/api/v4/alert-invites/{token}")]
    [InlineData("POST", "/api/v4/alert-invites/{0}/redeem", "/api/v4/alert-invites/{token}/redeem")]
    [InlineData("GET", "/api/v4/chat-identity/links/pending/{0}", "/api/v4/chat-identity/links/pending/{token}")]
    public async Task CredentialRoute_LeavesNoTokenInLogsScopesOrSpans(
        string method,
        string pathFormat,
        string redactedPath
    )
    {
        var token = $"noc_{Guid.NewGuid():N}";

        var (logs, spans) = await SendAsync(method, string.Format(pathFormat, token), redactedPath);

        logs.Should().Contain(l => l.ScopeValues.Contains(redactedPath), "the hosting scope carries the path");
        logs.Should().Contain(l => l.Message.StartsWith("Request starting") && l.Message.Contains(redactedPath));
        logs.Should().Contain(l => l.AttributeValues.Contains(redactedPath), "the request line carries the path");
        logs.SelectMany(l => l.AllText).Should().NotContain(text => text.Contains(token));
        spans.SelectMany(s => s.AllText).Should().NotContain(text => text.Contains(token));
    }

    [Theory]
    [InlineData("token", "?token={0}&count=1", "?token={token}&count=1")]
    [InlineData("secret", "?count=1&secret={0}", "?count=1&secret={secret}")]
    [InlineData("TOKEN", "?TOKEN={0}", "?TOKEN={token}")]
    [InlineData("access_token", "?access_token={0}", "?access_token={access_token}")]
    public async Task QueryCredential_LeavesNoValueInLogsScopesOrSpans(string name, string queryFormat, string redactedQuery)
    {
        var guid = Guid.NewGuid().ToString("N");
        var secret = $"noc_{guid}%2Fz";

        var (logs, spans) = await SendAsync("GET", "/api/v1/entries" + string.Format(queryFormat, secret), "/api/v1/entries");

        logs.Should().Contain(
            l => l.Message.StartsWith("Request starting") && l.Message.Contains("/api/v1/entries" + redactedQuery),
            $"the {name} value is replaced and the parameter name kept"
        );
        logs.SelectMany(l => l.AllText).Should().NotContain(text => text.Contains(guid));
        spans.SelectMany(s => s.AllText).Should().NotContain(text => text.Contains(guid));
    }

    [Fact]
    public void UriLogAttribute_HasItsQueryCredentialRedacted()
    {
        var guid = Guid.NewGuid().ToString("N");
        var uri = new Uri($"https://nightscout.example/api/v1/entries?count=1&token=noc_{guid}");
        var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Nocturne.Tests.UriAttribute");

        logger.LogWarning("Sending request to Nightscout: {Uri}", uri);

        var log = _factory.Logs.Single(l => l.Message.StartsWith("Sending request to Nightscout"));
        log.Message.Should().Contain("?count=1&token={token}");
        log.AllText.Should().NotContain(text => text.Contains(guid));
    }

    [Fact]
    public void RecordMixingARedactedValueAndAnUntouchedUri_LogsWithoutThrowing()
    {
        var guid = Guid.NewGuid().ToString("N");
        var clean = new Uri("https://nightscout.example/api/v1/entries?count=1");
        var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Nocturne.Tests.MixedAttributes");

        var log = () => logger.LogWarning("Forwarding {Path} to {Uri}", $"?token=noc_{guid}", clean);

        log.Should().NotThrow();
        var record = _factory.Logs.Single(l => l.Message.StartsWith("Forwarding"));
        record.Message.Should().Be("Forwarding ?token={token} to https://nightscout.example/api/v1/entries?count=1");
        record.AllText.Should().NotContain(text => text.Contains(guid));
    }

    [Theory]
    [InlineData("/api/v1/entries?count=1&find[type]=sgv", null)]
    [InlineData("/api/v1/entries?tokens=abc&mytoken=abc", null)]
    [InlineData("/api/v1/entries", null)]
    [InlineData("?token=", null)]
    [InlineData("/api/v1/entries?a=1&token=abc&b=2#top", "/api/v1/entries?a=1&token={token}&b=2#top")]
    [InlineData("/api/v1/entries?Secret=a%20b&token=c", "/api/v1/entries?Secret={secret}&token={token}")]
    [InlineData("?token=abc", "?token={token}")]
    [InlineData("/hubs/data?access_token=eyJ.abc.def", "/hubs/data?access_token={access_token}")]
    [InlineData("?a=#&token=abc", "?a=#&token={token}")]
    [InlineData("?%74oken=abc&se%63ret=def", "?%74oken={token}&se%63ret={secret}")]
    [InlineData("/api/v1/entries?token=abc&token=def", "/api/v1/entries?token={token}&token={token}")]
    public void Query_RedactsOnlyCredentialValues(string input, string? expected)
    {
        var redacted = new CredentialPathRedactor().TryRedact(input, out var result);

        redacted.Should().Be(expected is not null);
        result.Should().Be(expected);
    }

    [Fact]
    public void CredentialRouteWithQueryCredential_RedactsBoth()
    {
        var redactor = CredentialRouteRedactor.Create();

        redactor.Redact("/api/v4/alert-invites/abc?token=def")
            .Should().Be("/api/v4/alert-invites/{token}?token={token}");
    }

    [Fact]
    public async Task TokenExchange_RejectionWarning_CarriesTheRedactedPathScope()
    {
        const string redactedPath = "/api/v2/authorization/request/{accessToken}";
        var token = $"noc_{Guid.NewGuid():N}";

        var (logs, _) = await SendAsync("GET", $"/api/v2/authorization/request/{token}", redactedPath);

        var warning = logs.Should()
            .ContainSingle(l => l.Message == "JWT generation failed - invalid access token")
            .Subject;
        warning.ScopeValues.Should().Contain(redactedPath);
        warning.AllText.Should().NotContain(text => text.Contains(token));
    }

    [Fact]
    public async Task NonCredentialRoute_KeepsItsPathInLogsAndSpans()
    {
        var path = $"/api/v4/member-invites/members/{Guid.NewGuid()}/effective-permissions";

        var (logs, spans) = await SendAsync("GET", path, path);

        logs.Should().Contain(l => l.ScopeValues.Contains(path));
        spans.Should().Contain(s => s.UrlPath == path);
    }

    /// <summary>
    /// An outbound span, such as the compatibility proxy forwarding the token exchange upstream,
    /// records the absolute URL rather than a path.
    /// </summary>
    [Fact]
    public void ClientSpan_AbsoluteUrl_HasItsCredentialPathRedacted()
    {
        var token = $"noc_{Guid.NewGuid():N}";
        var url = $"https://nightscout.example/api/v2/authorization/request/{token}?count=1#top";
        using var source = new ActivitySource("System.Net.Http");

        using (var activity = source.StartActivity("GET", ActivityKind.Client))
        {
            activity.Should().NotBeNull("the tracer listens to HttpClient spans");
            activity!.SetTag("url.full", url);
        }

        var span = _factory.Spans.Single(s => s.AllText.Any(t => t.Contains("nightscout.example")));
        span.Tags.Should().Contain("https://nightscout.example/api/v2/authorization/request/{accessToken}?count=1#top");
        span.AllText.Should().NotContain(text => text.Contains(token));
    }

    /// <summary>
    /// Scans every route, not only controller actions: <see cref="CredentialPathRedactor"/> reads
    /// <see cref="CredentialRouteValueAttribute"/> from controller actions alone, so a credential
    /// parameter on any other endpoint cannot be marked and fails here.
    /// </summary>
    [Fact]
    public void EveryCredentialNamedRouteParameter_IsMarked()
    {
        var unmarked = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => e.RoutePattern.Parameters
                .Where(p => CredentialName().IsMatch(p.Name)
                    && !NotCredentials.ContainsKey($"{e.RoutePattern.RawText} {p.Name}")
                    && !IsMarked(e, p.Name))
                .Select(p => $"{e.RoutePattern.RawText} {p.Name}"))
            .Distinct()
            .ToList();

        unmarked.Should().BeEmpty();
    }

    [Fact]
    public void CredentialRouteValue_IsOnlyUsedOnControllerActions()
    {
        var elsewhere = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is null
                && e.Metadata.GetMetadata<MethodInfo>() is { } method
                && method.GetParameters().Any(p => p.GetCustomAttribute<CredentialRouteValueAttribute>() is not null))
            .Select(e => e.DisplayName)
            .ToList();

        elsewhere.Should().BeEmpty();
    }

    [Fact]
    public void EveryMarkedRouteParameter_IsRedactedFromItsPath()
    {
        var redactor = _factory.Services.GetRequiredService<CredentialPathRedactor>();
        var marked = ControllerEndpoints()
            .Where(e => e.Action.MethodInfo.GetParameters()
                .Any(p => p.GetCustomAttribute<CredentialRouteValueAttribute>() is not null))
            .ToList();

        marked.Should().HaveCountGreaterThanOrEqualTo(6);
        foreach (var (endpoint, action) in marked)
        {
            var path = "/" + string.Join('/', endpoint.RoutePattern.PathSegments.Select(s =>
                s.Parts.Single() switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart parameter => $"sample-{parameter.Name}",
                    _ => throw new InvalidOperationException(endpoint.RoutePattern.RawText),
                }));

            redactor.TryRedact(path, out var redacted).Should().BeTrue(path);
            foreach (var parameter in action.MethodInfo.GetParameters()
                .Where(p => p.GetCustomAttribute<CredentialRouteValueAttribute>() is not null))
            {
                redacted.Should().NotContain($"sample-{parameter.Name}").And.Contain($"{{{parameter.Name}}}");
            }
        }
    }

    /// <summary>
    /// Sends one request and returns the telemetry it produced. The server span and the closing
    /// request line can land after the response does, so the capture waits for a span whose
    /// <c>url.path</c> is <paramref name="expectedPath"/> before it is read.
    /// </summary>
    private async Task<(List<CapturedLog> Logs, List<CapturedSpan> Spans)> SendAsync(
        string method,
        string path,
        string expectedPath
    )
    {
        var logsBefore = _factory.Logs.Count;
        var spansBefore = _factory.Spans.Count;

        await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_factory.Spans.Skip(spansBefore).Any(s => s.UrlPath == expectedPath)
            || !_factory.Logs.Skip(logsBefore).Any(l => l.Message.StartsWith("Request finished")))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the request's server span and closing request line are captured");
            await Task.Delay(20);
        }

        return (_factory.Logs.Skip(logsBefore).ToList(), _factory.Spans.Skip(spansBefore).ToList());
    }

    [GeneratedRegex("token|secret|password|nonce|apikey|code$|key$|hash|sig|otp|ticket", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialName();

    /// <summary>Route parameters the name heuristic matches that are not credentials.</summary>
    private static readonly Dictionary<string, string> NotCredentials = new()
    {
        ["api/v4/coach-marks/{key} key"] = "names a coach mark in the UI, not a credential",
    };

    private static bool IsMarked(RouteEndpoint endpoint, string parameterName) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.MethodInfo.GetParameters()
            .Any(p => string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase)
                && p.GetCustomAttribute<CredentialRouteValueAttribute>() is not null) == true;

    private IEnumerable<(RouteEndpoint Endpoint, ControllerActionDescriptor Action)> ControllerEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Action: e.Metadata.GetMetadata<ControllerActionDescriptor>()))
            .Where(e => e.Action is not null)
            .Select(e => (e.Endpoint, e.Action!));

    public sealed record CapturedLog(string Message, List<string> AttributeValues, List<string> ScopeValues)
    {
        public IEnumerable<string> AllText => [Message, .. AttributeValues, .. ScopeValues];
    }

    public sealed record CapturedSpan(string DisplayName, string? UrlPath, List<string> Tags)
    {
        public IEnumerable<string> AllText => [DisplayName, .. Tags];
    }

    /// <summary>
    /// Captures what the OpenTelemetry exporters would receive, with the hosting request lines
    /// raised to Information as an operator debugging a request would.
    /// </summary>
    public sealed class TelemetryCaptureFactory : AuthenticationTestFactory
    {
        public ConcurrentQueue<CapturedLog> Logs { get; } = new();

        public ConcurrentQueue<CapturedSpan> Spans { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(new LogCapture(Logs)));
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SpanCapture(Spans)));
            });
        }

        protected override void ConfigureTestLogging(ILoggingBuilder logging)
        {
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            logging.AddFilter("Nocturne", LogLevel.Warning);
        }
    }

    private sealed class LogCapture(ConcurrentQueue<CapturedLog> logs) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            var scopes = new List<string>();
            data.ForEachScope(
                (scope, values) =>
                {
                    values.Add(scope.Scope?.ToString() ?? "");
                    foreach (var value in scope)
                    {
                        values.Add(value.Value?.ToString() ?? "");
                    }
                },
                scopes
            );

            logs.Enqueue(new CapturedLog(
                data.FormattedMessage ?? "",
                data.Attributes?.Select(a => a.Value?.ToString() ?? "").ToList() ?? [],
                scopes
            ));
        }
    }

    private sealed class SpanCapture(ConcurrentQueue<CapturedSpan> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) =>
            spans.Enqueue(new CapturedSpan(
                data.DisplayName,
                data.GetTagItem("url.path") as string,
                data.TagObjects.Select(t => t.Value?.ToString() ?? "").ToList()
            ));
    }
}
