using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class DemoServiceHealthMonitorTests
{
    /// <summary>
    /// The cleanup soft-deletes demo glucose through the audited repositories, so its DI scope has to
    /// carry system attribution: a user-attributed delete stamps <c>deleted_by_user</c> and
    /// permanently blocks the demo rows from being re-seeded once the service recovers.
    /// </summary>
    [Fact]
    public async Task CleanupDemoData_RunsUnderSystemAttribution()
    {
        var cleanupCalled = new TaskCompletionSource();
        bool? ambientIsSystem = null;
        bool? contextIsSystem = null;
        string? contextEndpoint = null;

        var services = new ServiceCollection();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(sp => new TenantReadContext(null, null, sp.GetRequiredService<IAuditContext>()));
        services.AddScoped<IDataSourceService>(sp =>
        {
            var ambient = sp.GetRequiredService<IAuditContext>();
            var readContext = sp.GetRequiredService<TenantReadContext>();
            var stub = new Mock<IDataSourceService>();
            stub.Setup(x => x.DeleteDemoDataAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    ambientIsSystem = ambient.IsSystem;
                    contextIsSystem = readContext.AuditContext?.IsSystem;
                    contextEndpoint = readContext.AuditContext?.Endpoint;
                    cleanupCalled.TrySetResult();
                    return Task.FromResult(new DataSourceDeleteResult { Success = true });
                });
            return stub.Object;
        });

        var monitor = new DemoServiceHealthMonitor(
            services.BuildServiceProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DemoService:Enabled"] = "true",
                ["DemoService:Url"] = "http://demo.invalid",
                ["DemoService:FailureThreshold"] = "1",
                ["DemoService:HealthCheckIntervalSeconds"] = "60",
            }).Build(),
            new StubHttpClientFactory(HttpStatusCode.ServiceUnavailable),
            NullLogger<DemoServiceHealthMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);
        // The body is thread-pool scheduled, so wait for the cleanup before stopping the host.
        await cleanupCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await monitor.StopAsync(CancellationToken.None);

        ambientIsSystem.Should().BeTrue();
        contextIsSystem.Should().BeTrue("every context of the scope reads the scope's audit context");
        contextEndpoint.Should().Be(DemoServiceHealthMonitor.AuditEndpoint);
    }

    private sealed class StubHttpClientFactory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(status));

        private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
