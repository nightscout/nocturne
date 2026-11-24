using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.API.Extensions;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

/// <summary>
/// The site URL's query is a credential. The named client the proxy sends through must not write it
/// into the factory's request log or into the HttpClient span.
/// </summary>
public class NightscoutClientRedactionTests
{
    /// <summary>Answers one request on the loopback so the request goes through the real socket handler, which is where the span is made.</summary>
    private static async Task AnswerOnceAsync(TcpListener listener)
    {
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose() { }

        private sealed class Capture(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    lock (owner.Lines)
                        owner.Lines.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
                }

                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines)
                {
                    owner.Lines.Add(formatter(state, exception));
                    if (state is IEnumerable<KeyValuePair<string, object?>> values)
                        owner.Lines.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
                }
            }
        }
    }

    [Fact]
    public async Task A_request_through_the_named_client_writes_no_query_to_a_log_or_a_span()
    {
        var logs = new CapturingProvider();
        var tags = new List<string>();
        using var spans = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (tags)
                {
                    tags.AddRange(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"));
                    tags.Add(activity.DisplayName);
                }
            },
        };
        ActivitySource.AddActivityListener(spans);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddCompatibilityProxyServices(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var answered = AnswerOnceAsync(listener);
        var site = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("NightscoutClient");
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, site + "/site/api/v1/status?count=1&token=abc123"));
        await answered;

        Assert.True(response.IsSuccessStatusCode);
        lock (logs.Lines)
        {
            Assert.Contains(logs.Lines, line => line.Contains("127.0.0.1"));
            Assert.All(logs.Lines, line => Assert.DoesNotContain("abc123", line));
        }

        lock (tags)
        {
            Assert.Contains(tags, tag => tag.StartsWith("url.full=", StringComparison.Ordinal));
            Assert.All(tags, tag => Assert.DoesNotContain("abc123", tag));
        }
    }
}
