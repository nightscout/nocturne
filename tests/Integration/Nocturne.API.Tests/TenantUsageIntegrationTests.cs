using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The operator's usage summary, read across tenants under row level security.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class TenantUsageIntegrationTests : ApiIntegrationTestBase
{
    private const string Route = "/api/v4/admin/tenants/usage-summary";
    private const string ErrorText = "HttpRequestException: Response status code does not indicate success: 401 (Unauthorized). owner@example.com";
    private const string OwnerEmail = "owner-usage@example.com";
    private const string OwnerName = "Usage Owner Person";
    private const string MemberAgent = "UsageAgent/1.0 (secret-device)";
    private const string MemberIp = "203.0.113.77";

    public TenantUsageIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task EachTenantReportsOnlyItsOwnUsage()
    {
        var now = DateTime.UtcNow;
        var busy = await SeedTenantAsync("busy");
        var quiet = await SeedTenantAsync("quiet");
        await SeedBusyTenantAsync(busy, now);
        await AuthTestHelpers.SeedAlertRuleAsync(Fixture, quiet, "Quiet rule");

        using var client = InstanceKeyClient();
        var summary = await GetAsync(client, $"{Route}?limit=500");

        summary.GetProperty("generatedAt").GetDateTime().Should().BeCloseTo(now, TimeSpan.FromMinutes(1));
        var tenants = summary.GetProperty("tenants").EnumerateArray().ToList();
        tenants.Select(t => t.GetProperty("tenantId").GetGuid()).Should().Contain([busy, quiet, Fixture.TenantId]);

        var b = tenants.Single(t => t.GetProperty("tenantId").GetGuid() == busy);
        b.GetProperty("isActive").GetBoolean().Should().BeTrue();
        b.GetProperty("lastReadingOn").GetString().Should().Be(Day(now.AddMinutes(-5)));
        b.GetProperty("onboardingCompletedOn").GetString().Should().Be(Day(now.AddDays(-20)));
        b.GetProperty("setupHubEnrolledOn").ValueKind.Should().Be(JsonValueKind.Null);
        b.GetProperty("lastOwnerLoginOn").GetString().Should().Be(Day(now.AddHours(-2)));
        b.GetProperty("lastMemberActivityOn").GetString().Should().Be(Day(now.AddHours(-1)));
        b.GetProperty("memberCount").GetInt32().Should().Be(2, "the Public subject and an ended member are not people with access");
        b.GetProperty("membersActiveLast7Days").GetInt32().Should().Be(1);

        var connectors = b.GetProperty("connectors").EnumerateArray().ToList();
        connectors.Select(c => c.GetProperty("type").GetString()).Should().Equal("dexcom", "glooko", "librelinkup", "tidepool");
        connectors.Select(c => c.GetProperty("status").GetString()).Should().Equal("failing", "healthy", "never_synced", "stale");
        connectors[0].GetProperty("enabled").GetBoolean().Should().BeTrue();
        connectors[0].GetProperty("errorCategory").GetString().Should().Be("auth");
        connectors[0].GetProperty("lastErrorOn").GetString().Should().Be(Day(now.AddMinutes(-30)));
        connectors[0].GetProperty("lastSuccessfulSyncOn").ValueKind.Should().Be(JsonValueKind.Null);
        connectors[1].GetProperty("enabled").GetBoolean().Should().BeFalse();
        connectors[1].GetProperty("errorCategory").GetString().Should().Be("none");
        connectors[1].GetProperty("lastSuccessfulSyncOn").GetString().Should().Be(Day(now.AddHours(-3)));
        connectors[1].GetProperty("lastErrorOn").ValueKind.Should().Be(JsonValueKind.Null);

        b.GetProperty("alertRuleCount").GetInt32().Should().Be(1);
        b.GetProperty("alertChannelTypes").EnumerateArray().Select(c => c.GetString()).Should().Equal("in_app", "webhook");
        b.GetProperty("savedReportCount").GetInt32().Should().Be(1, "a deleted report is not counted");
        b.GetProperty("trackerCount").GetInt32().Should().Be(1);
        b.GetProperty("clockFaceCount").GetInt32().Should().Be(1);
        b.GetProperty("guestLinkCount").GetInt32().Should().Be(1, "a revoked guest link is not counted");
        b.GetProperty("publicShareEnabled").GetBoolean().Should().BeTrue();
        b.GetProperty("shareLastAccessedOn").GetString().Should().Be(Day(now.AddDays(-1)));

        var devices = b.GetProperty("clientDevices").EnumerateArray().ToList();
        devices.Select(d => d.GetProperty("kind").GetString()).Should().Equal("companion", "prelude");
        devices[0].GetProperty("lastSeenOn").GetString().Should().Be(Day(now.AddMinutes(-10)));

        b.GetProperty("chatIdentityLinked").GetBoolean().Should().BeTrue();
        b.GetProperty("oauthClientCount").GetInt32().Should().Be(1);
        b.GetProperty("coachMarksSeenCount").GetInt32().Should().Be(1);

        var q = tenants.Single(t => t.GetProperty("tenantId").GetGuid() == quiet);
        q.GetProperty("alertRuleCount").GetInt32().Should().Be(1);
        q.GetProperty("alertChannelTypes").EnumerateArray().Select(c => c.GetString()).Should().Equal("in_app");
        q.GetProperty("memberCount").GetInt32().Should().Be(0);
        q.GetProperty("connectors").GetArrayLength().Should().Be(0);
        q.GetProperty("clientDevices").GetArrayLength().Should().Be(0);
        q.GetProperty("guestLinkCount").GetInt32().Should().Be(0);
        q.GetProperty("chatIdentityLinked").GetBoolean().Should().BeFalse();
        q.GetProperty("publicShareEnabled").GetBoolean().Should().BeFalse();
        q.GetProperty("lastOwnerLoginOn").ValueKind.Should().Be(JsonValueKind.Null);

    }

    [Fact]
    public async Task TheSummaryCarriesNoTimeOfDayErrorTextNamesEmailsAddressesOrAgents()
    {
        var busy = await SeedTenantAsync("private");
        await SeedBusyTenantAsync(busy, DateTime.UtcNow);

        using var client = InstanceKeyClient();
        var response = await client.GetAsync($"{Route}?tenantId={busy}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        JsonDocument.Parse(body).RootElement.GetProperty("tenants")[0].ToString().Should()
            .NotMatchRegex(@"T\d\d:\d\d", "no time of day about a site leaves the API");
        body.Should().NotContain("Unauthorized").And.NotContain("example.com")
            .And.NotContain(OwnerName).And.NotContain(MemberAgent).And.NotContain(MemberIp)
            .And.NotContain("Usage display name");
    }

    [Fact]
    public async Task TheTenantFilterAndPagingNarrowTheList()
    {
        var a = await SeedTenantAsync("page-a");
        var b = await SeedTenantAsync("page-b");
        var demo = await SeedTenantAsync("demo");
        await using (var conn = await OpenPostgresAsync())
            await conn.ExecuteAsync("UPDATE tenants SET is_demo = true WHERE id = @id", ("id", demo));

        using var client = InstanceKeyClient();

        var one = await GetAsync(client, $"{Route}?tenantId={a}");
        one.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("tenantId").GetGuid()).Should().Equal(a);

        (await GetAsync(client, $"{Route}?tenantId={demo}")).GetProperty("tenants").GetArrayLength()
            .Should().Be(0, "a demo tenant holds synthetic data");

        var all = (await GetAsync(client, $"{Route}?limit=500")).GetProperty("tenants").EnumerateArray()
            .Select(t => t.GetProperty("tenantId").GetGuid()).ToList();
        all.Should().BeInAscendingOrder(Comparer<Guid>.Create((x, y) => x.CompareTo(y)))
            .And.Contain([a, b]).And.NotContain(demo);

        var pages = new List<Guid>();
        for (var offset = 0; ; offset += 1)
        {
            var page = (await GetAsync(client, $"{Route}?limit=1&offset={offset}")).GetProperty("tenants");
            pages.AddRange(page.EnumerateArray().Select(t => t.GetProperty("tenantId").GetGuid()));
            if (page.GetArrayLength() < 1)
                break;
        }

        pages.Should().Equal(all);
    }

    [Fact]
    public async Task OnlyTheInstanceKeyMayRead()
    {
        using var anonymous = Fixture.CreateApexClient();
        (await anonymous.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await AuthenticatedClient.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a tenant owner is not the operator");

        using var client = InstanceKeyClient();
        (await client.GetAsync($"{Route}?limit=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static string Day(DateTime utc) => DateOnly.FromDateTime(utc).ToString("yyyy-MM-dd");

    private async Task<Guid> SeedTenantAsync(string prefix) =>
        await AuthTestHelpers.SeedTenantAsync(Fixture, $"{prefix}{Random.Shared.Next(1_000_000)}", "Usage display name");

    /// <summary>
    /// An owner, a member active 10 days ago, an ended member, two connectors, alerts, reports,
    /// trackers, a clock face, guest links, devices, a chat link, an OAuth client and coach marks.
    /// </summary>
    private async Task SeedBusyTenantAsync(Guid tenantId, DateTime now)
    {
        var owner = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        var ended = Guid.CreateVersion7();

        await using (var db = Fixture.CreateDbContext(tenantId))
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            tenant.LastReadingAt = now.AddMinutes(-5);
            tenant.OnboardingCompletedAt = now.AddDays(-20);
            tenant.ShareToken = new string('a', 64);
            tenant.ShareLastAccessedAt = now.AddDays(-1);

            var ownerSubject = new SubjectEntity { Id = owner, Name = OwnerName, IsActive = true, LastLoginAt = now.AddHours(-2) };
            ownerSubject.SetEmail(OwnerEmail);
            db.Subjects.AddRange(
                ownerSubject,
                new SubjectEntity { Id = member, Name = "Usage Member", IsActive = true, LastLoginAt = now.AddMinutes(-1) },
                new SubjectEntity { Id = ended, Name = "Usage Ended", IsActive = true, LastLoginAt = now });

            var ownerRole = await db.TenantRoles.SingleAsync(r => r.TenantId == tenantId && r.Slug == RoleSeeds.Owner);
            var ownerMembership = new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = owner, LastUsedAt = now.AddHours(-1),
                LastUsedIp = MemberIp, LastUsedUserAgent = MemberAgent,
            };
            db.TenantMembers.AddRange(
                ownerMembership,
                new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = member, LastUsedAt = now.AddDays(-10) },
                new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = ended, LastUsedAt = now,
                    AccessEndsAt = now.AddDays(-1),
                });
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(), TenantMemberId = ownerMembership.Id, TenantRoleId = ownerRole.Id,
            });

            db.ConnectorConfigurations.AddRange(
                new ConnectorConfigurationEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectorName = "dexcom",
                    IsHealthy = false, LastErrorMessage = ErrorText, LastErrorAt = now.AddMinutes(-30),
                },
                new ConnectorConfigurationEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectorName = "glooko",
                    ConfigurationJson = """{"enabled":false}""", LastSuccessfulSync = now.AddHours(-3),
                },
                new ConnectorConfigurationEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectorName = "librelinkup" },
                new ConnectorConfigurationEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectorName = "tidepool",
                    LastSuccessfulSync = now.AddHours(-7),
                });

            db.ReportSpecs.AddRange(
                new ReportSpecEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Kept", CreatedAt = now, UpdatedAt = now },
                new ReportSpecEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Gone", CreatedAt = now, UpdatedAt = now,
                    DeletedAt = now,
                });
            db.TrackerDefinitions.Add(new TrackerDefinitionEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Sensor" });
            db.ClockFaces.Add(new ClockFaceEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, UserId = owner.ToString(), Name = "Clock" });
            db.ClientDevices.AddRange(
                new ClientDeviceEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = owner, InstallId = "a", Kind = "companion", Label = "Usage laptop", LastSeenAt = now.AddDays(-2) },
                new ClientDeviceEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = owner, InstallId = "b", Kind = "companion", LastSeenAt = now.AddMinutes(-10) },
                new ClientDeviceEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = member, InstallId = "c", Kind = "prelude", LastSeenAt = now.AddDays(-3) });
            db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
            {
                Id = Guid.CreateVersion7(), Platform = "discord", PlatformUserId = "usage-1", TenantId = tenantId,
                NocturneUserId = owner, Label = "usage", DisplayName = "Usage display name",
            });
            db.OAuthClients.Add(new OAuthClientEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, ClientId = $"usage-{Guid.NewGuid():N}" });
            db.CoachMarkStates.AddRange(
                new CoachMarkStateEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = owner, MarkKey = "seen", Status = "seen", SeenAt = now },
                new CoachMarkStateEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = owner, MarkKey = "unseen" });
            await db.SaveChangesAsync();
        }

        var rule = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, tenantId);
        await using (var db = Fixture.CreateDbContext(tenantId))
        {
            db.AlertRuleChannels.Add(new AlertRuleChannelEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, AlertRuleId = rule,
                ChannelType = ChannelType.Webhook, Destination = "https://hooks.example.com/usage",
            });
            await db.SaveChangesAsync();
        }

        await using (var conn = await OpenPostgresAsync())
        {
            await AuthTestHelpers.SeedGuestLinkAsync(conn, owner);
            await AuthTestHelpers.SeedGuestLinkAsync(conn, owner, revokedAt: now.AddHours(-1));
        }
    }

    private HttpClient InstanceKeyClient()
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Add("X-Instance-Key", HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey));
        client.DefaultRequestHeaders.Add("X-Instance-Service", "nocturne-billing");
        return client;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
