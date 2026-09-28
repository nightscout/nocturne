using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Xunit;

namespace Nocturne.Aspire.ServiceDefaults.Tests.Logging;

public class ServiceDefaultsLoggingTests
{
    private const string OtlpEndpoint = "http://127.0.0.1:1";

    private static IHost BuildHost(
        string environment,
        string otlpEndpoint = "",
        Dictionary<string, string?>? settings = null
    )
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = environment }
        );

        var values = new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint,
        };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }
        builder.Configuration.AddInMemoryCollection(values);

        builder.AddServiceDefaults();
        return builder.Build();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("Polly")]
    public void QuietCategory_DefaultsToWarning(string category)
    {
        using var host = BuildHost(Environments.Production);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("Polly")]
    public void QuietCategory_IsRaisedByConfigurationOfTheSameCategory(string category)
    {
        using var host = BuildHost(
            Environments.Production,
            settings: new() { [$"Logging:LogLevel:{category}"] = "Debug" }
        );
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        logger.IsEnabled(LogLevel.Debug).Should().BeTrue();
    }

    [Theory]
    [InlineData("Production", "")]
    [InlineData("Production", OtlpEndpoint)]
    [InlineData("Development", OtlpEndpoint)]
    public void OpenTelemetry_IsTheOnlyLoggerProvider(string environment, string otlpEndpoint)
    {
        using var host = BuildHost(environment, otlpEndpoint);

        host.Services.GetServices<ILoggerProvider>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<OpenTelemetryLoggerProvider>();
    }

    [Theory]
    [InlineData("Production", "", true)]
    [InlineData("Development", OtlpEndpoint, true)]
    [InlineData("Production", OtlpEndpoint, false)]
    public void Console_IsWrittenUnlessOtlpIsConfiguredOutsideDevelopment(
        string environment,
        string otlpEndpoint,
        bool expectConsole
    )
    {
        var marker = $"console-probe-{Guid.NewGuid():N}";
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using (var host = BuildHost(environment, otlpEndpoint))
            {
                host.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Nocturne.Probe")
                    .LogWarning("{Marker}", marker);
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        captured.ToString().Contains(marker).Should().Be(expectConsole);
    }
}
