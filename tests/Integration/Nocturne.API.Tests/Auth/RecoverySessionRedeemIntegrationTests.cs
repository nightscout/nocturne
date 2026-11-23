using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The redemptions that start a recovery session or confirm an address, over the real pipeline. A
/// redemption writes through a second context while the claim's transaction holds its write, which
/// only a database with row locks serves.
/// </summary>
/// <remarks>
/// An anonymous caller on a site's own host carries an empty permission trie, so the seeded tenant
/// is a private site. A stuck follower is a member with no way to sign in on a site its owner can
/// still reach; recovery mode is the owner unable to sign in.
/// </remarks>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class RecoverySessionRedeemIntegrationTests : ApiIntegrationTestBase
{
    public RecoverySessionRedeemIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public enum Site
    {
        Private,
        WithStuckFollower,
        InRecoveryMode,
    }

    public static TheoryData<Site> Sites => [Site.Private, Site.WithStuckFollower, Site.InRecoveryMode];

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task Redeeming_an_emailed_recovery_link_is_admitted_and_starts_a_session(Site site)
    {
        var arranged = await ArrangeAsync(site);
        var code = await IssueAsync(arranged.EmailMemberId, LoginCodePurpose.EmailRecovery, arranged.Email);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        var response = await PostAsync(client, "/api/auth/passkey/recovery-mode/redeem", new { code }, null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie")
            .Should().Contain(h => h.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task Redeeming_a_confirmation_link_is_admitted(Site site)
    {
        var arranged = await ArrangeAsync(site, confirmed: false);
        var code = await IssueAsync(arranged.EmailMemberId, LoginCodePurpose.EmailConfirm, arranged.Email);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        var response = await PostAsync(client, "/api/auth/email-confirmation/redeem", new { code }, null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_stuck_follower_spends_an_administrators_code_and_enrols_on_a_site_that_is_not_in_recovery_mode()
    {
        var arranged = await ArrangeAsync(Site.WithStuckFollower);
        var code = await IssueAsync(arranged.StuckFollowerId!.Value, LoginCodePurpose.Recovery);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        var redeemed = await PostAsync(client, "/api/auth/passkey/recovery-mode/redeem", new { code }, null);

        redeemed.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = redeemed.Headers.GetValues("Set-Cookie")
            .Single(h => h.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));
        var session = setCookie[(setCookie.IndexOf('=') + 1)..setCookie.IndexOf(';')];
        var options = await PostAsync(
            client, "/api/auth/passkey/register/options", new { username = arranged.StuckFollowerUsername }, session);
        options.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_operators_code_for_a_member_who_still_has_a_passkey_starts_a_session_that_can_enrol_another()
    {
        var arranged = await ArrangeAsync(Site.Private);
        var code = await IssueAsync(arranged.EmailMemberId, LoginCodePurpose.OperatorRecovery);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        var redeemed = await PostAsync(client, "/api/auth/passkey/recovery-mode/redeem", new { code }, null);

        redeemed.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = redeemed.Headers.GetValues("Set-Cookie")
            .Single(h => h.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));
        var session = setCookie[(setCookie.IndexOf('=') + 1)..setCookie.IndexOf(';')];
        var options = await PostAsync(
            client, "/api/auth/passkey/register/options", new { username = arranged.EmailMemberUsername }, session);
        options.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record Arranged(
        Guid EmailMemberId, string EmailMemberUsername, string Email, Guid? StuckFollowerId, string StuckFollowerUsername);

    /// <summary>
    /// A member holding an address of its own, confirmed unless told otherwise, and a passkey. With
    /// <see cref="Site.WithStuckFollower"/> a follower who cannot sign in joins too; in recovery mode
    /// the owner's passkey is set aside, so the site's only member administrator is stuck. The
    /// cleanup between tests removes the members and gives the owner a passkey back.
    /// </summary>
    private async Task<Arranged> ArrangeAsync(Site site, bool confirmed = true)
    {
        var memberId = Guid.CreateVersion7();
        var suffix = memberId.ToString("N");
        var email = $"recovering-{suffix}@example.test";
        var arranged = new Arranged(
            memberId, $"recovering-{suffix}", email,
            site == Site.WithStuckFollower ? Guid.CreateVersion7() : null, $"stuck-follower-{suffix}");

        await WithTenantScopeAsync(async services =>
        {
            await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            var subject = new SubjectEntity
            {
                Id = memberId, Name = "Recovering", Username = arranged.EmailMemberUsername, IsActive = true,
            };
            subject.SetEmail(email);
            if (confirmed)
                subject.ConfirmEmail(email, DateTime.UtcNow);
            db.Subjects.Add(subject);
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, SubjectId = memberId,
            });
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = memberId, CredentialId = memberId.ToByteArray(), PublicKey = [1],
                CreatedAt = DateTime.UtcNow,
            });

            if (arranged.StuckFollowerId is { } followerId)
            {
                db.Subjects.Add(new SubjectEntity
                {
                    Id = followerId, Name = "Stuck follower", Username = arranged.StuckFollowerUsername, IsActive = true,
                });
                db.TenantMembers.Add(new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, SubjectId = followerId,
                    DirectPermissions = [Scope.GlucoseRead],
                });
            }

            if (site == Site.InRecoveryMode)
            {
                await db.PasskeyCredentials.Where(c => c.SubjectId == Fixture.OwnerSubjectId).ExecuteDeleteAsync();
            }
            await db.SaveChangesAsync();
            return true;
        });

        return arranged;
    }

    private Task<string> IssueAsync(Guid subjectId, LoginCodePurpose purpose, string? email = null) =>
        WithTenantScopeAsync(async services =>
        {
            await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            var codes = services.GetRequiredService<ILoginCodeService>();
            var actor = new AuthAuditActor(null, "test");
            return email is null
                ? (await codes.IssueAsync(db, subjectId, purpose, null, null, actor)).Code
                : (await codes.IssueEmailedAsync(db, subjectId, purpose, email, null, null, actor))!.Code;
        });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (session != null)
            request.Headers.Add("Cookie", $"{RecoverySession.CookieName}={session}");
        return client.SendAsync(request);
    }
}
