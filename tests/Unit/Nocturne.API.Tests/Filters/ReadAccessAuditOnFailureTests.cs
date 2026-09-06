using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Health;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Filters;

/// <summary>
/// Pins that a V4 PHI read is audited whether it succeeds or the action throws.
/// </summary>
/// <remarks>
/// The trail is compliance-relevant, so a failed read has to leave a row too — and a throwing
/// action is precisely the case MVC answers by skipping the result pipeline, which is where the
/// audit row is written from on every other path.
/// </remarks>
[Trait("Category", "Unit")]
public class ReadAccessAuditOnFailureTests
    : IClassFixture<ReadAccessAuditOnFailureTests.AuditingThrowingFactory>
{
    private const string ThrowingEndpoint = "GET /api/v4/body-weight";
    private const string NotFoundEndpoint = "GET /api/v4/body-weight/missing";

    private readonly HttpClient _client;
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;

    public ReadAccessAuditOnFailureTests(AuditingThrowingFactory factory)
    {
        _contextFactory = factory.Services.GetRequiredService<
            IDbContextFactory<NocturneDbContext>
        >();
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            "api-secret",
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(AuthenticationTestFactory.ApiSecret)))
                .ToLowerInvariant()
        );
    }

    [Fact]
    public async Task ThrowingRead_IsAuditedWithStatus500()
    {
        var response = await _client.GetAsync("/api/v4/body-weight");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var entry = await WaitForEntryAsync(ThrowingEndpoint);
        entry.Should().NotBeNull("a failed PHI read still has to leave an audit trail");
        entry!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task NonThrowingRead_IsStillAuditedWithItsOwnStatus()
    {
        var response = await _client.GetAsync("/api/v4/body-weight/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var entry = await WaitForEntryAsync(NotFoundEndpoint);
        entry.Should().NotBeNull();
        entry!.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// The audit write is fire-and-forget, so it can land after the response.
    /// </summary>
    private async Task<ReadAccessLogEntity?> WaitForEntryAsync(string endpoint)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            db.TenantId = TestDatabaseSeeder.TenantId;
            var entry = await db
                .ReadAccessLog.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Endpoint == endpoint);
            if (entry is not null)
                return entry;

            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// Makes the collection read throw, so one route on the controller fails while the sibling
    /// by-id route still answers normally.
    /// </summary>
    public sealed class AuditingThrowingFactory : AuthenticationTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                var auditConfig = new Mock<ITenantAuditConfigCache>();
                auditConfig
                    .Setup(c => c.GetConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TenantAuditConfig(ReadAuditEnabled: true, null, null));
                services.AddSingleton(auditConfig.Object);

                var bodyWeight = new Mock<IBodyWeightService>();
                bodyWeight
                    .Setup(s =>
                        s.GetBodyWeightsAsync(
                            It.IsAny<int>(),
                            It.IsAny<int>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .ThrowsAsync(new InvalidOperationException("boom from the service layer"));
                services.AddSingleton(bodyWeight.Object);
            });
        }
    }
}
