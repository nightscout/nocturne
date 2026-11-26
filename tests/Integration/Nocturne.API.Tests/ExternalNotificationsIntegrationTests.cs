using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The two routes that put notices from outside Nocturne in members' trays: the operator's, with
/// the instance key, and a connected app's, with <c>notifications.send</c>.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ExternalNotificationsIntegrationTests : ApiIntegrationTestBase
{
    private const string AppRedirect = "https://app.notifier.example/callback";

    private OperatorConfiguration _operator = null!;
    private string? _savedName;
    private List<string> _savedDomains = [];

    public ExternalNotificationsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _operator = Fixture.Services.GetRequiredService<IOptions<OperatorConfiguration>>().Value;
        _savedName = _operator.Name;
        _savedDomains = _operator.NotificationLinkDomains;
        _operator.Name = "Test Operator";
        _operator.NotificationLinkDomains = ["operator.example"];
    }

    public override async Task DisposeAsync()
    {
        _operator.Name = _savedName;
        _operator.NotificationLinkDomains = _savedDomains;
        await base.DisposeAsync();
    }

    // ---- operator ----

    [Fact]
    public async Task TheOperatorNotifiesTheOwnersOnceAndTheOwnerSeesItAsInformation()
    {
        var key = Key();
        using var client = InstanceKeyClient();

        var first = await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(key, "owners",
            link: new { label = "See your plan", url = "https://billing.operator.example/plan" }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(first);
        body.GetProperty("recipientCount").GetInt32().Should().Be(1);
        var recipient = body.GetProperty("recipients")[0];
        recipient.GetProperty("subjectId").GetGuid().Should().Be(Fixture.OwnerSubjectId);
        recipient.GetProperty("status").GetString().Should().Be("created");
        var notificationId = recipient.GetProperty("notificationId").GetGuid();

        var repeat = await JsonAsync(await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(key, "owners")));
        repeat.GetProperty("recipients")[0].GetProperty("notificationId").GetGuid().Should().Be(notificationId);

        var tray = await TrayAsync(Fixture.OwnerSubjectId);
        var notice = tray.Single(n => n.GetProperty("id").GetGuid() == notificationId);
        notice.GetProperty("type").GetString().Should().Be("external.operator");
        notice.GetProperty("urgency").GetString().Should().Be("Info");
        notice.GetProperty("category").GetString().Should().Be("Informational");
        notice.GetProperty("metadata").GetProperty("senderName").GetString().Should().Be("Test Operator");
        notice.GetProperty("metadata").GetProperty("linkUrl").GetString().Should().Be("https://billing.operator.example/plan");
        tray.Count(n => n.GetProperty("sourceId").GetString() == key).Should().Be(1);
    }

    [Fact]
    public async Task TheOperatorWithdrawsANotice()
    {
        var key = Key();
        using var client = InstanceKeyClient();
        var sent = await JsonAsync(await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(key, "owners")));
        var notificationId = sent.GetProperty("recipients")[0].GetProperty("notificationId").GetGuid();

        var withdrawn = await client.DeleteAsync($"{OperatorRoute(Fixture.TenantId)}/{key}");

        withdrawn.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(withdrawn)).GetProperty("withdrawnCount").GetInt32().Should().Be(1);
        (await TrayAsync(Fixture.OwnerSubjectId)).Should().NotContain(n => n.GetProperty("id").GetGuid() == notificationId);

        var resent = await JsonAsync(await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(key, "owners")));
        resent.GetProperty("recipients")[0].GetProperty("notificationId").GetGuid().Should().Be(notificationId,
            "a withdrawn key stays used");
        (await TrayAsync(Fixture.OwnerSubjectId)).Should().NotContain(n => n.GetProperty("id").GetGuid() == notificationId);
    }

    [Fact]
    public async Task ARepeatAfterTheNoticeWasCleanedUpIsAlreadySent()
    {
        var key = Key();
        using var client = InstanceKeyClient();
        var sent = await JsonAsync(await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(key, "owners")));
        var notificationId = sent.GetProperty("recipients")[0].GetProperty("notificationId").GetGuid();

        await using (var conn = await OpenPostgresAsync())
        await using (var transaction = await conn.BeginTransactionAsync())
        {
            await conn.PinTenantAsync(Fixture.TenantId, local: true);
            await conn.ExecuteAsync("DELETE FROM in_app_notifications WHERE id = @id", ("id", notificationId));
            await transaction.CommitAsync();
        }

        var again = await JsonAsync(await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(key, "owners")));

        var recipient = again.GetProperty("recipients")[0];
        recipient.GetProperty("status").GetString().Should().Be("alreadySent");
        (await TrayAsync(Fixture.OwnerSubjectId)).Should().NotContain(n => n.GetProperty("sourceId").GetString() == key);
    }

    [Fact]
    public async Task ATenantWithNoOwnerGetsAnOwnersNoticeNowhere()
    {
        var tenantId = await AuthTestHelpers.SeedTenantAsync(Fixture, $"noowner{Random.Shared.Next(1_000_000)}", "No owner");
        using var client = InstanceKeyClient();

        var response = await client.PostAsJsonAsync(OperatorRoute(tenantId), Notice(Key(), "owners"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("recipientCount").GetInt32().Should().Be(0);
        body.GetProperty("recipients").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownTenantIsNotFound()
    {
        using var client = InstanceKeyClient();

        var response = await client.PostAsJsonAsync(OperatorRoute(Guid.NewGuid()), Notice(Key(), "owners"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("tenant_not_found");
    }

    [Fact]
    public async Task AnInactiveTenantIsAConflict()
    {
        var tenantId = await AuthTestHelpers.SeedTenantAsync(Fixture, $"inactive{Random.Shared.Next(1_000_000)}", "Inactive");
        await using (var conn = await OpenPostgresAsync())
            await conn.ExecuteAsync("UPDATE tenants SET is_active = false WHERE id = @id", ("id", tenantId));
        using var client = InstanceKeyClient();

        var response = await client.PostAsJsonAsync(OperatorRoute(tenantId), Notice(Key(), "owners"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("tenant_inactive");
    }

    [Fact]
    public async Task WithNoOperatorNameTheOperatorCannotSend()
    {
        _operator.Name = null;
        using var client = InstanceKeyClient();

        var response = await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(Key(), "owners"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("operator_not_configured");
    }

    [Fact]
    public async Task TheOperatorLinksOnlyToItsDomains()
    {
        using var client = InstanceKeyClient();

        var response = await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(Key(), "owners",
            link: new { label = "Open", url = "https://elsewhere.example/" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("link_domain_not_allowed");
    }

    [Fact]
    public async Task AnOwnersSessionIsNotTheOperator()
    {
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId);
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, session);

        var response = await client.PostAsJsonAsync(OperatorRoute(Fixture.TenantId), Notice(Key(), "owners"));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ---- connected apps ----

    [Fact]
    public async Task AnAppAPlainMemberGrantedNotifiesOnlyThatMember()
    {
        var (member, app) = await SeedMemberAppAsync(["glucose.read"]);

        var response = await app.PostAsJsonAsync(AppRoute, Notice(Key(), audience: null,
            link: new { label = "Open", url = "https://app.notifier.example/open" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("recipientCount").GetInt32().Should().Be(1);
        body.GetProperty("recipients")[0].GetProperty("subjectId").GetGuid().Should().Be(member);

        var notice = (await TrayAsync(member)).Single(n =>
            n.GetProperty("id").GetGuid() == body.GetProperty("recipients")[0].GetProperty("notificationId").GetGuid());
        notice.GetProperty("type").GetString().Should().Be("external.app");
        notice.GetProperty("metadata").GetProperty("senderKind").GetString().Should().Be("app");

        var members = await app.PostAsJsonAsync(AppRoute, Notice(Key(), "members"));
        members.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(members)).GetProperty("error").GetString().Should().Be("audience_not_allowed");
    }

    [Fact]
    public async Task AnAppAnOwnerGrantedNotifiesTheMembers()
    {
        var (member, _) = await SeedMemberAppAsync(["glucose.read"]);
        using var app = await AppClientAsync(Fixture.OwnerSubjectId);

        var response = await app.PostAsJsonAsync(AppRoute, Notice(Key(), "members"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipients = (await JsonAsync(response)).GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("subjectId").GetGuid()).ToList();
        recipients.Should().Contain([Fixture.OwnerSubjectId, member]);
    }

    [Fact]
    public async Task AnAppLinksOnlyToItsRedirectDomains()
    {
        var (_, app) = await SeedMemberAppAsync(["glucose.read"]);

        var response = await app.PostAsJsonAsync(AppRoute, Notice(Key(), audience: null,
            link: new { label = "Open", url = "https://operator.example/" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("link_domain_not_allowed");
    }

    [Fact]
    public async Task AnAppWithoutTheScopeIsRefused()
    {
        var (_, app) = await SeedMemberAppAsync(["glucose.read"], scope: "glucose.read");

        var response = await app.PostAsJsonAsync(AppRoute, Notice(Key(), audience: null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APersonsOwnSignInIsNotAnApp()
    {
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId);
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, session);

        var response = await client.PostAsJsonAsync(AppRoute, Notice(Key(), audience: null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(response)).GetProperty("error").GetString().Should().Be("oauth_app_required");
    }

    [Fact]
    public async Task AnAppSendsFiveNewKeysADayAndWithdrawsItsOwn()
    {
        var (member, app) = await SeedMemberAppAsync(["glucose.read"]);
        var keys = Enumerable.Range(0, 5).Select(_ => Key()).ToList();
        foreach (var key in keys)
            (await app.PostAsJsonAsync(AppRoute, Notice(key, audience: null))).StatusCode.Should().Be(HttpStatusCode.OK);

        var limited = await app.PostAsJsonAsync(AppRoute, Notice(Key(), audience: null));
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await JsonAsync(limited)).GetProperty("error").GetString().Should().Be("rate_limited");

        (await app.PostAsJsonAsync(AppRoute, Notice(keys[0], audience: null))).StatusCode
            .Should().Be(HttpStatusCode.OK, "a repeat of a key already sent does not count");

        var withdrawn = await app.DeleteAsync($"{AppRoute}/{keys[0]}");
        withdrawn.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(withdrawn)).GetProperty("withdrawnCount").GetInt32().Should().Be(1);
        (await TrayAsync(member)).Should().NotContain(n => n.GetProperty("sourceId").GetString() == keys[0]);

        using var operatorClient = InstanceKeyClient();
        (await operatorClient.DeleteAsync($"{OperatorRoute(Fixture.TenantId)}/{keys[1]}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the operator cannot withdraw an app's notice");
    }

    // ---- helpers ----

    private const string AppRoute = "/api/v4/notifications/external";

    private static string OperatorRoute(Guid tenantId) => $"/api/v4/admin/tenants/{tenantId}/notifications";

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private static object Notice(string key, string? audience, object? link = null) => new
    {
        audience,
        key,
        title = "A notice from outside",
        body = "Plain text, as written.",
        link,
    };

    private HttpClient InstanceKeyClient()
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Add("X-Instance-Key", HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey));
        client.DefaultRequestHeaders.Add("X-Instance-Service", "nocturne-billing");
        return client;
    }

    /// <summary>A member with <paramref name="permissions"/> and no role, and an app they granted.</summary>
    private async Task<(Guid Member, HttpClient App)> SeedMemberAppAsync(
        IReadOnlyList<string> permissions, string scope = Scope.NotificationsSend)
    {
        await using var conn = await OpenPostgresAsync();
        var (member, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
            conn, Fixture.TenantId, $"Member {Guid.NewGuid():N}", permissions);
        return (member, await AppClientAsync(member, scope));
    }

    /// <summary>A client presenting the access token of an app <paramref name="subjectId"/> granted.</summary>
    private async Task<HttpClient> AppClientAsync(Guid subjectId, string scope = Scope.NotificationsSend)
    {
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var consenting = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(consenting, AppRedirect, scope);
        var flow = await AuthTestHelpers.ExecutePkceFlowAsync(consenting, clientId, AppRedirect, scope);
        return AuthTestHelpers.CreateBearerClient(Fixture, flow.AccessToken);
    }

    private async Task<List<JsonElement>> TrayAsync(Guid subjectId)
    {
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var response = await client.GetAsync("/api/v4/notifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await JsonAsync(response)).EnumerateArray().ToList();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
