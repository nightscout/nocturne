using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.DevOnly;
using Nocturne.API.Services.Platform.InstanceKey;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Shared.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class InstanceKeyChangeIntegrationTests : ApiIntegrationTestBase
{
    private static readonly ISecretEncryptionService OldKey = new SecretEncryptionService(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ServiceNames.ConfigKeys.InstanceKey] = "a-key-this-instance-lost" })
            .Build(),
        NullLogger<SecretEncryptionService>.Instance);

    public InstanceKeyChangeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task EachTenantScanSeesOnlyItsOwnSecretsThatNoLongerDecrypt()
    {
        var contexts = Fixture.Services.GetRequiredService<INocturneDbContextFactory>();
        var other = await SeedTenantAsync();
        var connector = $"keycheck{Random.Shared.Next(1_000_000)}";
        var ruleId = Guid.CreateVersion7();

        await using (var db = await contexts.CreateForTenantAsync(Fixture.TenantId))
        {
            db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
            {
                Id = Guid.CreateVersion7(),
                ConnectorName = connector,
                SecretsJson = JsonSerializer.Serialize(OldKey.EncryptSecrets(new() { ["password"] = "hunter2" })),
            });
            var rule = new AlertRuleEntity { Id = ruleId, Name = "Key check rule" };
            rule.Channels.Add(new AlertRuleChannelEntity
            {
                Id = Guid.CreateVersion7(), AlertRuleId = ruleId, ChannelType = ChannelType.Webhook,
                Destination = "https://hooks.example.com/hook", Secret = OldKey.Encrypt("signing-secret"),
            });
            db.AlertRules.Add(rule);
            await db.SaveChangesAsync();
        }

        try
        {
            var mine = await ScanAsync(Fixture.TenantId, "integration");
            var theirs = await ScanAsync(other, "keycheck-other");

            mine.Connectors.Should().ContainSingle(c => c.ConnectorName == connector);
            mine.AlertChannels.Should().ContainSingle()
                .Which.Should().Be(new UndecryptableAlertChannel(ruleId, "Key check rule", "hooks.example.com"));
            theirs.IsEmpty.Should().BeTrue("RLS keeps one tenant's rows out of another tenant's scan");
        }
        finally
        {
            await using var db = await contexts.CreateForTenantAsync(Fixture.TenantId);
            await db.AlertRules.Where(r => r.Id == ruleId).ExecuteDeleteAsync();
            await db.ConnectorConfigurations.Where(c => c.ConnectorName == connector).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task AnotherKeyStartsAChangeThatTheReplacedKeyLeavesAlone()
    {
        var contexts = Fixture.Services.GetRequiredService<INocturneDbContextFactory>();
        var appKey = Fixture.Services.GetRequiredService<InstanceKeyCheck>();
        var otherKey = new InstanceKeyCheck(contexts, OldKey);

        (await appKey.FindChangeAsync(CancellationToken.None)).State.Should().Be(InstanceKeyState.Current);
        var started = await GenerationAsync(contexts);

        try
        {
            var other = await otherKey.FindChangeAsync(CancellationToken.None);
            var replaced = await appKey.FindChangeAsync(CancellationToken.None);
            var again = await otherKey.FindChangeAsync(CancellationToken.None);

            other.State.Should().Be(InstanceKeyState.Changed);
            other.Generation.Should().NotBe(started);
            replaced.Should().Be(new InstanceKeyFinding(InstanceKeyState.Superseded, other.Generation));
            again.Should().Be(other);

            await using (var db = await contexts.CreatePlatformAsync())
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE instance_key_check SET checked_at = now() - interval '11 minutes'");
            var rollback = await appKey.FindChangeAsync(CancellationToken.None);

            rollback.State.Should().Be(InstanceKeyState.Changed, "the newer key stopped scanning, by the database clock");
            rollback.Generation.Should().NotBe(other.Generation);
            (await otherKey.FindChangeAsync(CancellationToken.None)).State.Should().Be(InstanceKeyState.Superseded);
        }
        finally
        {
            var encryption = Fixture.Services.GetRequiredService<ISecretEncryptionService>();
            await using var db = await contexts.CreatePlatformAsync();
            await db.InstanceKeyChecks.ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Ciphertext, encryption.Encrypt(InstanceKeyCheck.KnownValue))
                .SetProperty(c => c.PreviousCiphertext, (string?)null)
                .SetProperty(c => c.Outstanding, false));
        }

        (await appKey.FindChangeAsync(CancellationToken.None)).State.Should().Be(InstanceKeyState.Current);
    }

    private static async Task<Guid> GenerationAsync(INocturneDbContextFactory contexts)
    {
        await using var db = await contexts.CreatePlatformAsync();
        return (await db.InstanceKeyChecks.AsNoTracking().SingleAsync()).Generation;
    }

    [Fact]
    public async Task APlatformAdminIsFoundInTheTenantTheyJoined()
    {
        var contexts = Fixture.Services.GetRequiredService<INocturneDbContextFactory>();
        var service = Fixture.Services.GetServices<IHostedService>().OfType<InstanceKeyChangeService>().Single();
        var (tenantId, slug, ownerId) = await SeedTenantWithOwnerAsync();

        await using (var db = await contexts.CreatePlatformAsync())
            await db.Subjects.Where(s => s.Id == ownerId).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsPlatformAdmin, true));
        try
        {
            (await service.PlatformAdminsAsync(CancellationToken.None))
                .Should().ContainSingle(a => a.SubjectId == ownerId)
                .Which.Should().Be(new InstanceKeyChangeService.PlatformAdmin(ownerId, tenantId, slug, "Key check"));
        }
        finally
        {
            await using var db = await contexts.CreatePlatformAsync();
            await db.Subjects.Where(s => s.Id == ownerId).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsPlatformAdmin, false));
        }
    }

    private async Task<TenantUndecryptableSecrets> ScanAsync(Guid tenantId, string slug)
    {
        await using var scope = TenantJobScope.Open(
            Fixture.Services, new TenantContext(tenantId, slug, slug, true, IsDemo: false), "test:instance-key");
        return await scope.ServiceProvider.GetRequiredService<UndecryptableSecretScanner>()
            .ScanTenantAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedTenantAsync() => (await SeedTenantWithOwnerAsync()).TenantId;

    private async Task<(Guid TenantId, string Slug, Guid OwnerId)> SeedTenantWithOwnerAsync()
    {
        using var apex = Fixture.CreateApexClient();
        var slug = $"keycheck{Random.Shared.Next(1_000_000)}";
        using var response = await apex.PostAsJsonAsync(
            "/api/v4/dev-only/admin/seed-tenant", new DevSeedTenantRequest(slug, "Key check", $"{slug}-owner"));
        response.EnsureSuccessStatusCode();
        var seeded = (await response.Content.ReadFromJsonAsync<DevSeedTenantResponse>())!;
        return (seeded.TenantId, slug, seeded.SubjectId);
    }
}
