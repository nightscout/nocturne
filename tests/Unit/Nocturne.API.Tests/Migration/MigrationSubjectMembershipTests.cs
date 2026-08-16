using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A subject imported from a classic Nightscout instance must come out of the migration as a member
/// of the tenant it was imported into. Without the membership row it authenticates on its access
/// token and is then dropped straight back to unauthenticated by the membership check in
/// <c>AuthenticationMiddleware</c>, so every migrated <c>?token=</c> client 401s.
/// </summary>
public class MigrationSubjectMembershipTests
{
    private const string ReadableToken = "reader-a1b2c3d4e5f6a7b8";
    private const string AdminToken = "boss-1111222233334444";

    /// <summary>
    /// Stands in for the source Nightscout instance, serving its two authorization endpoints.
    /// Everything else answers 404, which the migration treats as "collection unavailable".
    /// </summary>
    private sealed class NightscoutStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/authorization/roles" => """
                    [
                      { "name": "readable", "permissions": ["*:*:read"] },
                      { "name": "admin", "permissions": ["*"] },
                      { "name": "logger", "permissions": ["api:treatments:*"] }
                    ]
                    """,
                "/api/v2/authorization/subjects" => $$"""
                    [
                      {
                        "_id": "5f1a00000000000000000001",
                        "name": "Reader",
                        "roles": ["readable", "logger"],
                        "accessToken": "{{ReadableToken}}"
                      },
                      {
                        "_id": "5f1a00000000000000000002",
                        "name": "Boss",
                        "roles": ["admin"],
                        "accessToken": "{{AdminToken}}"
                      }
                    ]
                    """,
                _ => null,
            };

            var response = body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NightscoutStub());
    }

    private sealed class FixedTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }

        public bool IsResolved => Context is not null;

        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public void SetTenant(TenantContext? tenant) => Context = tenant;
    }

    private static ServiceProvider BuildProvider(string databaseName) =>
        new ServiceCollection()
            .AddDbContext<NocturneDbContext>(o => o.UseInMemoryDatabase(databaseName))
            .AddScoped<ITenantAccessor, FixedTenantAccessor>()
            .AddScoped<IAuditContext, AuditContext>()
            .AddSingleton<IHttpClientFactory, StubHttpClientFactory>()
            .BuildServiceProvider();

    /// <summary>
    /// Runs a subjects-only API migration to completion against <see cref="NightscoutStub"/> and
    /// returns the tenant it imported into.
    /// </summary>
    private static async Task<Guid> RunSubjectMigrationAsync(IServiceProvider provider)
    {
        var tenant = new TenantContext(
            Guid.CreateVersion7(), "migrated", "Migrated Tenant", true, IsDemo: false);

        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = "https://example-nightscout.invalid",
            Collections = ["subjects"],
        };

        var job = new MigrationJob(
            Guid.CreateVersion7(),
            tenant.TenantId,
            request,
            new MigrationJobInfo
            {
                Id = Guid.CreateVersion7(),
                Mode = MigrationMode.Api,
                CreatedAt = DateTime.UtcNow,
            },
            tenant,
            NullLogger.Instance,
            provider);

        await job.ExecuteAsync(CancellationToken.None);
        job.GetStatus().State.Should().Be(MigrationJobState.Completed);

        return tenant.TenantId;
    }

    private static async Task<TenantMemberEntity> MemberForAsync(
        NocturneDbContext db, Guid tenantId, string accessTokenName)
    {
        var subject = await db.Subjects.SingleAsync(s => s.Name == accessTokenName);
        return await db.TenantMembers.SingleAsync(
            tm => tm.TenantId == tenantId && tm.SubjectId == subject.Id);
    }

    [Fact]
    public async Task Imported_subject_becomes_a_member_holding_its_Nightscout_permissions()
    {
        await using var provider = BuildProvider($"migration-membership-{Guid.NewGuid():N}");
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var member = await MemberForAsync(db, tenantId, "Reader");

        // "readable" carries the read scopes; the custom "logger" role's "api:treatments:*" is what
        // lets it write treatments back.
        member.DirectPermissions.Should().Contain(
            [TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsReadWrite]);
    }

    [Fact]
    public async Task Imported_membership_grants_scopes_on_the_legacy_access_token()
    {
        await using var provider = BuildProvider($"migration-membership-{Guid.NewGuid():N}");
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // The other half of the round trip: what MemberScopeMiddleware makes of the membership when
        // the imported token authenticates. An empty result is the 401 this migration used to cause.
        var resolved = MemberScopeResolver.Resolve(
            (await MemberForAsync(db, tenantId, "Reader")).DirectPermissions!.ToHashSet(),
            AuthType.LegacyAccessToken,
            new HashSet<string>());

        resolved.Should().Contain(OAuthScopes.GlucoseRead);
    }

    [Fact]
    public async Task A_Nightscout_admin_is_imported_as_a_superuser_member()
    {
        await using var provider = BuildProvider($"migration-membership-{Guid.NewGuid():N}");
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var member = await MemberForAsync(db, tenantId, "Boss");

        // Stored as the bare atom rather than the expansion, so the grant tracks the scope list.
        member.DirectPermissions.Should().Equal(TenantPermissions.Superuser);
    }

    [Fact]
    public async Task Re_importing_repairs_a_subject_that_has_no_membership()
    {
        await using var provider = BuildProvider($"migration-membership-{Guid.NewGuid():N}");

        // A subject as an earlier migration left it: imported, with the matching token hash, and
        // with no membership of the tenant it was imported into.
        var strandedId = Guid.CreateVersion7();
        using (var seedScope = provider.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            seed.Subjects.Add(new SubjectEntity
            {
                Id = strandedId,
                Name = "Reader",
                AccessTokenHash = Sha256Hex(ReadableToken),
                IsActive = true,
                ApprovalStatus = "Approved",
            });
            await seed.SaveChangesAsync();
        }

        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // Repaired in place: the duplicate token must not produce a second subject.
        db.Subjects.Should().ContainSingle(s => s.Name == "Reader");
        var member = await db.TenantMembers.SingleAsync(
            tm => tm.TenantId == tenantId && tm.SubjectId == strandedId);
        member.DirectPermissions.Should().Contain(TenantPermissions.GlucoseRead);
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
