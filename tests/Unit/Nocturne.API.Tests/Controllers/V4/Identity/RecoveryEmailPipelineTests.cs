using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A member's recovery email over the real pipeline: the fallback authorisation policy, tenant
/// resolution and the credential filters, which a controller called directly skips.
/// </summary>
/// <remarks>
/// An anonymous caller on a site's own host carries an empty permission trie, so the test host is
/// a private site. One factory, so one rate limiter: <c>recovery-email</c> allows five writes in
/// ten minutes across the class.
/// </remarks>
[Trait("Category", "Unit")]
public class RecoveryEmailPipelineTests : IClassFixture<RecoveryEmailPipelineTests.Factory>
{
    public sealed class Factory : AuthenticationTestFactory
    {
        public List<AccountLinkMessage> Sent { get; } = [];

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);
            services.AddSingleton<IAccountLinkDelivery>(new RecordingDelivery(Sent));
        }
    }

    private sealed class RecordingDelivery(List<AccountLinkMessage> sent) : IAccountLinkDelivery
    {
        public bool IsConfigured => true;

        public Task<bool> DeliverAsync(AccountLinkMessage message, CancellationToken ct)
        {
            lock (sent)
                sent.Add(message);
            return Task.FromResult(true);
        }
    }

    private const string Address = "pipeline@example.test";
    private const string Path = "/api/v4/me/recovery-email";

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public RecoveryEmailPipelineTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<NocturneDbContext> TenantDbAsync()
    {
        var db = await _factory.Services.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        db.TenantId = TestDatabaseSeeder.TenantId;
        return db;
    }

    private async Task<Guid> SeedMemberAsync(bool demo = false)
    {
        var id = Guid.CreateVersion7();
        await using var db = await TenantDbAsync();
        db.Subjects.Add(new SubjectEntity { Id = id, Name = "Pipeline member", IsActive = true, IsDemoSubject = demo });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TestDatabaseSeeder.TenantId, SubjectId = id,
            DirectPermissions = [Scope.GlucoseRead],
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<string> SessionCookieAsync(Guid subjectId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<ISessionService>()
            .IssueSessionAsync(subjectId, new SessionContext(DeviceDescription: "Recovery email test"));
        var cookieName = scope.ServiceProvider.GetRequiredService<IOptions<OidcOptions>>().Value.Cookie.AccessTokenName;
        return $"{cookieName}={session.AccessToken}";
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookie, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }

    private async Task<SubjectEntity> StoredAsync(Guid id)
    {
        await using var db = await TenantDbAsync();
        return await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    [Fact]
    public async Task AMemberOnAPrivateSiteSetsAnAddressWhichStaysUnconfirmedUntilTheLinkIsFollowed()
    {
        var id = await SeedMemberAsync();
        var cookie = await SessionCookieAsync(id);

        var put = await SendAsync(HttpMethod.Put, Path, cookie, new { email = Address });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var outcome = await put.Content.ReadFromJsonAsync<JsonElement>();
        outcome.GetProperty("sent").GetBoolean().Should().BeTrue();
        outcome.GetProperty("confirmed").GetBoolean().Should().BeFalse();
        var stored = await StoredAsync(id);
        stored.Email.Should().Be(Address);
        stored.EmailConfirmedAt.Should().BeNull();
        _factory.Sent.Should().ContainSingle(m => m.Email == Address && m.Kind == AccountLinkKind.EmailConfirmation)
            .Which.Link.Should().Contain("/auth/confirm-email?code=");

        var read = await (await SendAsync(HttpMethod.Get, Path, cookie)).Content.ReadFromJsonAsync<JsonElement>();
        read.GetProperty("available").GetBoolean().Should().BeTrue();
        read.GetProperty("email").GetString().Should().Be(Address);
        read.GetProperty("confirmed").GetBoolean().Should().BeFalse();

        var prompt = await (await SendAsync(HttpMethod.Get, "/api/v4/me/recovery-email-prompt", cookie))
            .Content.ReadFromJsonAsync<JsonElement>();
        prompt.GetProperty("show").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AnAnonymousCallerIsRefused()
    {
        (await SendAsync(HttpMethod.Put, Path, cookie: null, new { email = Address })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, Path, cookie: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheInstanceApiSecretCannotSetAMembersAddress()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Path) { Content = JsonContent.Create(new { email = Address }) };
        request.Headers.Add("api-secret", TestDatabaseSeeder.Sha1Hex(AuthenticationTestFactory.ApiSecret));

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AGrantHeldByAnAppCannotReadSetOrClearAMembersAddress()
    {
        var id = await SeedMemberAsync();
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IDirectGrantService>().CreateAsync(
                TestDatabaseSeeder.TenantId, id, "App", [Scope.FullAccess], null, false, null, null);
            token = created.Response!.Token;
        }

        async Task<HttpStatusCode> SendAsGrantAsync(HttpMethod method, object? body = null)
        {
            var request = new HttpRequestMessage(method, Path);
            request.Headers.Add("Authorization", $"Bearer {token}");
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return (await _client.SendAsync(request)).StatusCode;
        }

        (await SendAsGrantAsync(HttpMethod.Get)).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsGrantAsync(HttpMethod.Put, new { email = Address })).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsGrantAsync(HttpMethod.Delete)).Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(id)).Email.Should().BeNull();
    }

    [Fact]
    public async Task TheDemoSubjectCannotSetAnAddress()
    {
        var id = await SeedMemberAsync(demo: true);

        var put = await SendAsync(HttpMethod.Put, Path, await SessionCookieAsync(id), new { email = Address });

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(id)).Email.Should().BeNull();
    }
}
