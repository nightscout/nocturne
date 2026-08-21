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

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class DemoServiceHealthMonitorTests
{
    /// <summary>
    /// The cleanup soft-deletes demo glucose through the audited repositories, so its DI scope has to
    /// carry system attribution on both context paths: a user-attributed delete stamps
    /// <c>deleted_by_user</c> and permanently blocks the demo rows from being re-seeded once the
    /// service recovers.
    /// </summary>
    [Fact]
    public async Task CleanupDemoData_RunsUnderSystemAttribution_OnBothContextPaths()
    {
        var cleanupCalled = new TaskCompletionSource();
        bool? ambientIsSystem = null;
        bool? contextIsSystem = null;
        string? contextEndpoint = null;

        var services = new ServiceCollection();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(_ => new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite("Filename=:memory:")
                .Options));
        services.AddScoped<IDataSourceService>(sp =>
        {
            var ambient = sp.GetRequiredService<IAuditContext>();
            var dbContext = sp.GetRequiredService<NocturneDbContext>();
            var stub = new Mock<IDataSourceService>();
            stub.Setup(x => x.DeleteDemoDataAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    ambientIsSystem = ambient.IsSystem;
                    contextIsSystem = dbContext.AuditContext?.IsSystem;
                    contextEndpoint = dbContext.AuditContext?.Endpoint;
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

        ambientIsSystem.Should().BeTrue(
            "repositories stamp factory-created contexts from the ambient audit context");
        contextIsSystem.Should().BeTrue(
            "the interceptor prefers the scoped context's own audit context");
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
