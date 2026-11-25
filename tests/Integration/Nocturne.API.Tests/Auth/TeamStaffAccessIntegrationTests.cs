using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.API.Hubs;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A team's staff member who is no member of the site otherwise, over HTTP and the hubs: they read
/// nothing until the site approves the team's access, then read what it gives, limited to the last
/// 24 hours when the access is; the site cannot change their access, only the team's; when an owner
/// narrows the team's access their next request reads the narrower access; and when the site stops the
/// access their next request is refused. Either way their hub connection closes on revalidation.
/// Where the site decides its staff, an owner's approval and decline of one staff member do the same.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class TeamStaffAccessIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task A_staff_member_reads_the_site_once_it_approves_the_team_and_loses_it_at_once_when_it_stops()
    {
        var (manager, _) = await SeedSubjectAsync("Clinic Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Site Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Clinic Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        var staffSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, staff);
        using var staffOnSite = AuthTestHelpers.CreateBearerClient(Fixture, staffSession);
        var teamId = await TeamWithStaffAsync(managerClient, staff);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.OK);
        var closed = await SubscribedHubAsync(staffSession);

        var staffMember = (await MembersAsync(ownerOnSite)).Single(m => m.GetProperty("subjectId").GetGuid() == staff);
        staffMember.GetProperty("hasDirect").GetBoolean().Should().BeFalse();
        var changed = await ownerOnSite.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember.GetProperty("id").GetGuid()}/limit-to-24-hours", new { limitTo24Hours = true });
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("member_access_from_team");

        (await ownerOnSite.DeleteAsync($"/api/v4/member-invites/team-access/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await MembersAsync(ownerOnSite)).Should().NotContain(m => m.GetProperty("subjectId").GetGuid() == staff);
        await Fixture.Services.GetRequiredService<HubConnectionRevalidator>().RevalidateAsync(CancellationToken.None);
        (await Task.WhenAny(closed, Task.Delay(Eventually.DefaultPatience))).Should().BeSameAs(closed, "the staff member's access ended");
    }

    [Fact]
    public async Task A_waiting_staff_member_reads_nothing_until_an_owner_approves_them_then_reads_and_subscribes()
    {
        var (manager, _) = await SeedSubjectAsync("Pending Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Pending Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Pending Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        var staffSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, staff);
        using var staffOnSite = AuthTestHelpers.CreateBearerClient(Fixture, staffSession);
        var teamId = await TeamWithStaffAsync(managerClient, staff);

        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: false);

        var access = await TeamAccessAsync(ownerOnSite, teamId);
        access.GetProperty("teamManagesStaff").GetBoolean().Should().BeFalse();
        var listed = access.GetProperty("staff").EnumerateArray().Should().ContainSingle().Subject;
        (listed.GetProperty("subjectId").GetGuid(), listed.GetProperty("status").GetString()).Should().Be((staff, "pending"));
        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await ownerOnSite.PostAsync($"/api/v4/member-invites/team-access/{teamId}/staff/{staff}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.OK);
        await SubscribedHubAsync(staffSession);
    }

    [Fact]
    public async Task Declining_an_approved_staff_member_refuses_their_next_request_and_closes_their_hub_on_revalidation()
    {
        var (manager, _) = await SeedSubjectAsync("Declining Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Declining Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Declined Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        var staffSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, staff);
        using var staffOnSite = AuthTestHelpers.CreateBearerClient(Fixture, staffSession);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);
        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.OK);
        var closed = await SubscribedHubAsync(staffSession);

        (await ownerOnSite.PostAsync($"/api/v4/member-invites/team-access/{teamId}/staff/{staff}/decline", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await TeamAccessAsync(ownerOnSite, teamId)).GetProperty("staff").EnumerateArray().Single()
            .GetProperty("status").GetString().Should().Be("declined");
        await Fixture.Services.GetRequiredService<HubConnectionRevalidator>().RevalidateAsync(CancellationToken.None);
        (await Task.WhenAny(closed, Task.Delay(Eventually.DefaultPatience))).Should().BeSameAs(closed, "the staff member was declined");
    }

    [Fact]
    public async Task A_member_who_manages_members_but_is_no_owner_is_refused_and_an_unknown_team_is_not_found()
    {
        var (manager, _) = await SeedSubjectAsync("Refused Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Refused Owner", owner: true);
        string managingToken;
        await using (var conn = await OpenPostgresAsync())
        {
            (_, managingToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
                conn, await AuthTestHelpers.GetTenantIdAsync(conn), "Member Manager", [Scope.MembersManage]);
        }
        var staff = await SeedBareSubjectAsync("Refused Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        using var managingOnSite = AuthTestHelpers.CreateBearerClient(Fixture, managingToken);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);

        var refused = await managingOnSite.PostAsync($"/api/v4/member-invites/team-access/{teamId}/staff/{staff}/decline", null);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_staff_decision_not_owner");
        var refusedApproval = await managingOnSite.PostAsync($"/api/v4/member-invites/team-access/{teamId}/staff/{staff}/approve", null);
        refusedApproval.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refusedApproval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_staff_decision_not_owner");
        (await managingOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{teamId}/staff-rule", new { teamManagesStaff = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await ownerOnSite.PostAsync($"/api/v4/member-invites/team-access/{Guid.NewGuid()}/staff/{staff}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ownerOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{teamId}/staff-rule", new { teamManagesStaff = false }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TeamAccessAsync(ownerOnSite, teamId)).GetProperty("teamManagesStaff").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_narrows_a_teams_access_and_a_staff_members_next_request_and_hub_read_the_narrower_access()
    {
        var (manager, _) = await SeedSubjectAsync("Editing Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Editing Owner", owner: true);
        string managingToken;
        Guid viewer, admin;
        await using (var conn = await OpenPostgresAsync())
        {
            var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
            (_, managingToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Editing Member Manager", [Scope.MembersManage]);
            viewer = await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Viewer);
            admin = await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Admin);
        }
        var staff = await SeedBareSubjectAsync("Edited Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        using var managingOnSite = AuthTestHelpers.CreateBearerClient(Fixture, managingToken);
        var staffSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, staff);
        using var staffOnSite = AuthTestHelpers.CreateBearerClient(Fixture, staffSession);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);
        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.OK);
        var closed = await SubscribedHubAsync(staffSession);
        var narrowed = new { roleIds = new[] { viewer }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null };

        var notOwner = await managingOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{teamId}", narrowed);
        notOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await notOwner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_access_not_owner");
        (await ownerOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{Guid.NewGuid()}", narrowed))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var administrative = await ownerOnSite.PutAsJsonAsync(
            $"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { admin }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null });
        administrative.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await administrative.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_access_role_administrative");
        (await ownerOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { viewer } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ownerOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{teamId}", narrowed)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staffOnSite.GetAsync("/api/v1/entries.json?count=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TeamAccessAsync(ownerOnSite, teamId)).GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("slug").GetString()).Should().Equal(RoleSeeds.Viewer);
        await Fixture.Services.GetRequiredService<HubConnectionRevalidator>().RevalidateAsync(CancellationToken.None);
        (await Task.WhenAny(closed, Task.Delay(Eventually.DefaultPatience))).Should().BeSameAs(closed, "the team's access no longer gives alerts");
    }

    /// <remarks>
    /// A scoped token never reaches the edit's own bound on roles: <c>members.manage</c> is an administration
    /// atom no scoped credential resolves to (<see cref="MemberScopeResolver.Resolve"/>), so the scope check
    /// refuses it first. A token that holds every scope still carries its 24-hour limit to the edit.
    /// </remarks>
    [Fact]
    public async Task An_owners_edit_is_bounded_by_the_token_they_present()
    {
        var (manager, _) = await SeedSubjectAsync("Bounded Manager");
        var (owner, _) = await SeedSubjectAsync("Bounded Owner", owner: true);
        var (limitedOwner, limitedToken) = await SeedSubjectAsync("Limited Owner", owner: true);
        var (narrowOwner, narrowToken) = await SeedSubjectAsync("Narrow Owner", owner: true);
        await SetGrantAsync(limitedOwner, limitTo24Hours: true, scopes: ["*"]);
        await SetGrantAsync(narrowOwner, limitTo24Hours: false, scopes: [Scope.GlucoseRead]);
        var staff = await SeedBareSubjectAsync("Bounded Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var limitedOnSite = AuthTestHelpers.CreateBearerClient(Fixture, limitedToken);
        using var narrowOnSite = AuthTestHelpers.CreateBearerClient(Fixture, narrowToken);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);
        Guid viewer;
        await using (var conn = await OpenPostgresAsync())
        {
            viewer = await AuthTestHelpers.GetTenantRoleIdAsync(conn, await AuthTestHelpers.GetTenantIdAsync(conn), RoleSeeds.Viewer);
        }

        var unlimited = await limitedOnSite.PutAsJsonAsync(
            $"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { viewer }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null });
        unlimited.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unlimited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_access_limit_required");

        var scoped = await narrowOnSite.PutAsJsonAsync(
            $"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { viewer }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null });
        scoped.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scoped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("missing_scope");
        (await TeamAccessAsync(limitedOnSite, teamId)).GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("slug").GetString()).Should().Equal(RoleSeeds.Caretaker);

        (await limitedOnSite.PutAsJsonAsync(
                $"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { viewer }, limitTo24Hours = true, accessEndsAt = (DateTimeOffset?)null }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_owners_edit_that_sends_no_end_takes_away_the_end_of_the_access()
    {
        var (manager, _) = await SeedSubjectAsync("Endless Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Endless Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Endless Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);
        Guid viewer;
        await using (var conn = await OpenPostgresAsync())
        {
            viewer = await AuthTestHelpers.GetTenantRoleIdAsync(conn, await AuthTestHelpers.GetTenantIdAsync(conn), RoleSeeds.Viewer);
        }
        (await ownerOnSite.PutAsJsonAsync(
                $"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { viewer }, limitTo24Hours = false, accessEndsAt = _now.AddDays(30) }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TeamAccessAsync(ownerOnSite, teamId)).GetProperty("accessEndsAt").ValueKind.Should().Be(JsonValueKind.String);

        (await ownerOnSite.PutAsJsonAsync($"/api/v4/member-invites/team-access/{teamId}", new { roleIds = new[] { viewer }, limitTo24Hours = false }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TeamAccessAsync(ownerOnSite, teamId)).GetProperty("accessEndsAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task SetGrantAsync(Guid subject, bool limitTo24Hours, string[] scopes)
    {
        await using var conn = await OpenPostgresAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await conn.PinTenantAsync(await AuthTestHelpers.GetTenantIdAsync(conn), local: true);
        (await conn.ExecuteAsync(
            "UPDATE oauth_grants SET limit_to_24_hours = @limited, scopes = @scopes WHERE subject_id = @subject;",
            ("limited", limitTo24Hours), ("scopes", scopes), ("subject", subject))).Should().Be(1);
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Connects the alerts hub on the site as the holder of <paramref name="session"/> and subscribes;
    /// returns a task that completes when the connection closes.
    /// </summary>
    private async Task<Task> SubscribedHubAsync(string session)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(ApiClient.BaseAddress!, "hubs/alerts"), options =>
            {
                options.Headers["X-Forwarded-Host"] = Fixture.TenantHost;
                options.AccessTokenProvider = () => Task.FromResult<string?>(session);
            })
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .Build();
        HubConnections.Add(hub);
        await hub.StartAsync();
        await hub.InvokeAsync("Subscribe");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    private static async Task<JsonElement> TeamAccessAsync(HttpClient ownerOnSite, Guid teamId) =>
        (await ownerOnSite.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/team-access"))
            .EnumerateArray().Single(a => a.GetProperty("teamId").GetGuid() == teamId);

    [Fact]
    public async Task A_staff_member_on_an_access_limited_to_24_hours_reads_only_the_last_day()
    {
        var (manager, _) = await SeedSubjectAsync("Clamped Manager");
        var (owner, _) = await SeedSubjectAsync("Clamped Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Clamped Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var staffOnSite = AuthTestHelpers.CreateBearerClient(Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, staff));
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        var entries = new[] { _now.AddHours(-30), _now.AddHours(-1) }
            .Select(at => new { type = "sgv", sgv = 120, date = at.ToUnixTimeMilliseconds(), dateString = at.ToString("O") })
            .ToArray();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", entries)).EnsureSuccessStatusCode();
        (await DatesAsync(AuthenticatedClient)).Should().HaveCount(2, "the clamp below is only meaningful if the older entry is served at all");

        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Viewer, limitTo24Hours: true, teamManagesStaff: null);

        (await DatesAsync(staffOnSite)).Should().ContainSingle()
            .Which.Should().BeGreaterThan(_now.AddHours(-24).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_staff_member_reads_nothing_after_an_approval_where_the_site_decides_its_staff()
    {
        var (manager, _) = await SeedSubjectAsync("Waiting Manager");
        var (owner, _) = await SeedSubjectAsync("Waiting Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Waiting Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var staffOnSite = AuthTestHelpers.CreateBearerClient(Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, staff));
        var teamId = await TeamWithStaffAsync(managerClient, staff);

        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: false);

        (await staffOnSite.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_manager_narrows_a_staff_member_and_is_refused_an_end_that_has_passed()
    {
        var (manager, _) = await SeedSubjectAsync("Narrowing Manager");
        var staff = await SeedBareSubjectAsync("Narrowed Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        var end = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        (await managerClient.PutAsJsonAsync(
                $"/api/v4/me/teams/{teamId}/staff/{staff}", new { roleCeilingSlug = RoleSeeds.Viewer, limitTo24Hours = true, endsAt = end }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var passed = await managerClient.PutAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/staff/{staff}", new { roleCeilingSlug = RoleSeeds.Viewer, limitTo24Hours = true, endsAt = _now.AddDays(-1) });
        passed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await passed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_staff_end_invalid");

        var listed = (await managerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/{teamId}"))
            .GetProperty("staff").EnumerateArray().Should().ContainSingle().Subject;
        listed.GetRawText().Should().Contain(RoleSeeds.Viewer);
    }

    [Fact]
    public async Task An_owner_who_is_staff_of_a_team_the_site_approves_is_listed_once_as_owner_and_not_under_the_team()
    {
        var (manager, _) = await SeedSubjectAsync("Owning Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Owning Nurse", owner: true);
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        var teamId = await TeamWithStaffAsync(managerClient, owner);

        await ApproveAsync(managerClient, ownerClient, teamId, RoleSeeds.Caretaker, limitTo24Hours: false, teamManagesStaff: null);

        var listed = (await MembersAsync(ownerOnSite)).Should().ContainSingle(m => m.GetProperty("subjectId").GetGuid() == owner).Subject;
        listed.GetProperty("hasDirect").GetBoolean().Should().BeTrue();
        listed.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("slug").GetString()).Should().Contain(RoleSeeds.Owner);
        listed.GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("kind").GetString())
            .Should().Equal(nameof(MemberReasonKind.Direct));
        listed.GetProperty("reasons").EnumerateArray().Should().OnlyContain(r => r.GetProperty("teamId").ValueKind == JsonValueKind.Null);
    }

    /// <summary>A new team run by the manager, with <paramref name="staff"/> joined through a staff invite; returns the team.</summary>
    private async Task<Guid> TeamWithStaffAsync(HttpClient managerClient, Guid staff)
    {
        var teamId = (await (await managerClient.PostAsJsonAsync("/api/v4/me/teams", new { name = $"Clinic {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var invite = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/staff-invites", new { roleCeilingSlug = (string?)null, limitTo24Hours = false });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        using var staffAtApex = await SessionAtApexAsync(staff);
        (await staffAtApex.PostAsync($"/api/v4/me/teams/staff-invites/{token}/accept", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return teamId;
    }

    /// <summary>
    /// The site's owner approves the team's access link giving <paramref name="roleSlug"/>; a null
    /// <paramref name="teamManagesStaff"/> sends no choice, which leaves the team deciding its staff.
    /// </summary>
    private async Task ApproveAsync(
        HttpClient managerClient, HttpClient ownerClient, Guid teamId, string roleSlug, bool limitTo24Hours, bool? teamManagesStaff)
    {
        var link = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/access-links", new { roleSlug, limitTo24Hours });
        link.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var site = (await ownerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/access-links/{token}"))
            .GetProperty("sites").EnumerateArray().Single();
        var role = site.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("slug").GetString() == roleSlug).GetProperty("id").GetGuid();

        var approved = await ownerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/access-links/{token}/approve",
            new { tenantId = site.GetProperty("tenantId").GetGuid(), roleIds = new[] { role }, limitTo24Hours, accessEndsAt = (DateTimeOffset?)null, teamManagesStaff });
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<List<long>> DatesAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/v1/entries.json?count=10")).EnumerateArray().Select(e => e.GetProperty("date").GetInt64())];

    private static async Task<List<JsonElement>> MembersAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/members")).EnumerateArray()];

    private async Task<HttpClient> SessionAtApexAsync(Guid subject)
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
        return client;
    }

    /// <summary>A subject with no membership of any site and no credential of their own.</summary>
    private async Task<Guid> SeedBareSubjectAsync(string name)
    {
        var subject = Guid.CreateVersion7();
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync("""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, @name, true, false, now(), now(), 'Approved');
            """, ("id", subject), ("name", name));
        return subject;
    }

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(string name, bool owner = false)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var seeded = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name, [Scope.GlucoseRead]);
        if (owner)
        {
            await conn.ExecuteAsync("""
                INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
                SELECT gen_random_uuid(), id, @roleId FROM tenant_members
                 WHERE tenant_id = @tenantId AND subject_id = @subjectId;
                """, ("roleId", await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Owner)), ("tenantId", tenantId), ("subjectId", seeded.SubjectId));
        }

        return seeded;
    }
}
