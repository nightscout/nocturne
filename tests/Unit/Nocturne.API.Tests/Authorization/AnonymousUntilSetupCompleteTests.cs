using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The slug availability probe over the real pipeline: anonymous while the instance has no account
/// anyone can sign in with, authenticated callers only once it has.
/// </summary>
/// <remarks>
/// Driven through the routed endpoint rather than the filter in isolation, because the value of the
/// gate is that it runs — an <c>[AllowAnonymous]</c> endpoint suppresses policy evaluation, so a
/// guard expressed as a policy would read as protection and never be asked.
/// <para>
/// The seeded instance has an owner holding a passkey, so setup is complete unless a test says
/// otherwise. One factory, so one <c>name-availability</c> bucket (60 per minute) across the class.
/// </para>
/// </remarks>
public partial class AnonymousUntilSetupCompleteTests : IClassFixture<AuthenticationTestFactory>
{
    private const string SlugProbe = "/api/v4/me/tenants/validate-slug?slug=";
    private const string UsernameProbe = "/api/v4/setup/validate-username?username=";

    /// <summary>
    /// Both name-availability probes. They answer the same kind of question off the same rate
    /// limiter, so they carry the same gate — a username is the stronger of the two to confirm,
    /// being an account identifier rather than a public subdomain.
    /// </summary>
    public static TheoryData<string> Probes => [SlugProbe, UsernameProbe];

    private readonly AuthenticationTestFactory _factory;

    public AnonymousUntilSetupCompleteTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task An_anonymous_probe_is_refused_once_the_instance_has_an_account(string probe)
    {
        var response = await _factory.CreateClient().GetAsync($"{probe}anything");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task An_authenticated_probe_is_answered_once_the_instance_has_an_account(string probe)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await SessionCookieAsync());

        var response = await client.GetAsync($"{probe}anything");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task An_anonymous_probe_is_answered_while_the_instance_has_no_account(string probe)
    {
        var credentials = await TakeCredentialsAwayAsync();
        try
        {
            var response = await _factory.CreateClient().GetAsync($"{probe}anything");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await GiveCredentialsBackAsync(credentials);
        }
    }

    /// <summary>
    /// A demo session is handed to any anonymous caller, so the subject behind it stands for no one
    /// — "authenticated" here has to mean a person who signed up, or the gate is bypassed by anyone
    /// willing to take a demo session first.
    /// </summary>
    [Theory]
    [MemberData(nameof(Probes))]
    public async Task A_demo_session_is_refused_once_the_instance_has_an_account(string probe)
    {
        var demoSubjectId = await SeedDemoSubjectAsync();
        try
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", await SessionCookieAsync(demoSubjectId));

            var response = await client.GetAsync($"{probe}anything");

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await RemoveSubjectAsync(demoSubjectId);
        }
    }

    /// <summary>
    /// The refusal answers "who is asking", not "is this name free", so everything a caller can see
    /// of it — status, headers and body — is the same for a name in use as for one that is free.
    /// </summary>
    [Theory]
    [InlineData(SlugProbe, "default", "nobody-has-this-one")]
    [InlineData(UsernameProbe, "test", "nobody-has-this-one")]
    public async Task The_refusal_is_the_same_response_whether_the_name_is_taken_or_free(
        string probe, string takenName, string freeName)
    {
        await EnsureNameIsTakenAsync(probe, takenName);

        var client = _factory.CreateClient();

        var taken = await DescribeAsync(await client.GetAsync($"{probe}{takenName}"));
        var free = await DescribeAsync(await client.GetAsync($"{probe}{freeName}"));

        taken.Should().StartWith("401").And.NotContain(takenName);
        free.Should().NotContain(freeName);
        taken.Should().Be(free);
    }

    /// <summary>
    /// Makes <paramref name="takenName"/> genuinely in use, so the comparison is between a hit and
    /// a miss rather than between two misses. The seeded tenant already holds the slug; a username
    /// is held on the membership row rather than on the subject, and the membership row is what the
    /// username probe reads.
    /// </summary>
    private async Task EnsureNameIsTakenAsync(string probe, string takenName)
    {
        if (probe != UsernameProbe)
            return;

        await using var db = await DbAsync();

        var member = await db.TenantMembers
            .FirstAsync(m => m.SubjectId == TestDatabaseSeeder.TestSubjectId);
        member.Username = takenName;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Everything a caller can see of a response bar the clock and the per-request trace id:
    /// status, headers and body. Without the headers this would be comparing an empty string to an
    /// empty string on any response that carries no body.
    /// </summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key != "Date")
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");

        var described = string.Join("\n", headers
            .Prepend(((int)response.StatusCode).ToString())
            .Append(await response.Content.ReadAsStringAsync()));

        return TraceParent().Replace(described, "<trace>");
    }

    [GeneratedRegex("[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}")]
    private static partial Regex TraceParent();

    /// <summary>
    /// A cookie header carrying a real session for the seeded owner, which is how the authenticated
    /// admin page reaches this endpoint.
    /// </summary>
    private async Task<string> SessionCookieAsync(Guid? subjectId = null)
    {
        using var scope = _factory.Services.CreateScope();

        var session = await scope.ServiceProvider
            .GetRequiredService<ISessionService>()
            .IssueSessionAsync(
                subjectId ?? TestDatabaseSeeder.TestSubjectId,
                new SessionContext(DeviceDescription: "Gate test"));

        var cookieName = scope.ServiceProvider
            .GetRequiredService<IOptions<OidcOptions>>().Value.Cookie.AccessTokenName;

        return $"{cookieName}={session.AccessToken}";
    }

    private async Task<List<PasskeyCredentialEntity>> TakeCredentialsAwayAsync()
    {
        await using var db = await DbAsync();

        var credentials = await db.PasskeyCredentials.ToListAsync();
        db.PasskeyCredentials.RemoveRange(credentials);
        await db.SaveChangesAsync();

        return credentials;
    }

    private async Task GiveCredentialsBackAsync(List<PasskeyCredentialEntity> credentials)
    {
        await using var db = await DbAsync();

        db.PasskeyCredentials.AddRange(credentials);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedDemoSubjectAsync()
    {
        await using var db = await DbAsync();

        var subjectId = Guid.CreateVersion7();
        db.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Demo Visitor",
            Username = "demo-visitor",
            IsActive = true,
            IsSystemSubject = false,
            IsDemoSubject = true,
        });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestDatabaseSeeder.TenantId,
            SubjectId = subjectId,
        });
        await db.SaveChangesAsync();

        return subjectId;
    }

    private async Task RemoveSubjectAsync(Guid subjectId)
    {
        await using var db = await DbAsync();

        db.TenantMembers.RemoveRange(
            await db.TenantMembers.Where(m => m.SubjectId == subjectId).ToListAsync());
        db.Subjects.RemoveRange(
            await db.Subjects.Where(s => s.Id == subjectId).ToListAsync());
        await db.SaveChangesAsync();
    }

    private Task<NocturneDbContext> DbAsync() =>
        _factory.Services
            .GetRequiredService<IDbContextFactory<NocturneDbContext>>()
            .CreateDbContextAsync();
}
