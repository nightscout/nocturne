using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Migration;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Services.WriteBack;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout site's path and query can each be its credential, so neither the import's client, any
/// client of the Nightscout connector, nor the compatibility proxy's clients that reach a Nightscout
/// write a request URI to the log, as registered for the running API.
/// </summary>
public class NightscoutClientRequestLoggingTests
{
    private const string Secret = "AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string Token = "synthetic-token";
    private const string ControlClient = "connector-client-with-default-logging";

    public static TheoryData<string> NightscoutClients => new()
    {
        MigrationJobService.HttpClientName,
        nameof(NightscoutConnectorService),
        nameof(NightscoutEntryWriteBackSink),
        nameof(NightscoutTreatmentWriteBackSink),
        nameof(NightscoutDeviceStatusWriteBackSink),
        nameof(NightscoutProfileWriteBackSink),
        nameof(NightscoutFoodWriteBackSink),
        nameof(NightscoutActivityWriteBackSink),
        NightscoutConnectorInstaller.RealtimeHttpClientName,
        "NightscoutClient",
        CompatibilityProxyServiceExtensions.ManualTestHttpClientName,
    };

    [Theory]
    [MemberData(nameof(NightscoutClients))]
    public async Task A_request_through_the_client_logs_nothing_of_its_path(string client)
    {
        var lines = await SendThroughAsync(client);

        lines.Should().NotContain(line => line.Contains(Secret) || line.Contains(Token));
    }

    [Fact]
    public async Task A_connector_client_keeping_the_default_logging_would_have_logged_the_path()
    {
        var lines = await SendThroughAsync(ControlClient);

        lines.Should().Contain(line => line.Contains(Secret));
    }

    private static async Task<List<string>> SendThroughAsync(string client)
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddMigrationServices();
        services.AddCompatibilityProxyServices(new ConfigurationBuilder().Build());
        new NightscoutConnectorInstaller().Install(
            services,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Connectors:Nightscout:Enabled"] = "true" })
                .Build());
        services.AddHttpClient(ControlClient).ConfigureConnectorClient(baseUrl: null);

        // The link-local guard of a connector client resolves the host; the stub answers in its place.
        services.AddHttpClient(client)
            .ConfigurePrimaryHttpMessageHandler(() => new Answering())
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                foreach (var guard in handlers.OfType<LinkLocalGuardHandler>().ToList())
                    handlers.Remove(guard);
            });

        await using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(client);
        using var response = await http.GetAsync($"https://ns.example.com/{Secret}/api/v1/status.json?token={Token}");
        response.EnsureSuccessStatusCode();

        return capture.Lines;
    }

    private sealed class Answering : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    /// <summary>Records every message and every scope, since the factory's request scope names the URI too.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                lock (lines)
                    lines.Add(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                    lines.Add(formatter(state, exception));
            }
        }
    }
}
