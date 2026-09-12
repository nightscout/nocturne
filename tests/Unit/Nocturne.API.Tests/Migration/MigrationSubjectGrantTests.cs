using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A subject imported from a classic Nightscout instance is an API token, not a person, so it must
/// come out of the migration as a direct grant on the tenant's device subject rather than as an
/// account of its own. An account leaves a member with no way to sign in, which takes the whole
/// tenant into recovery mode with no way back out of it.
/// </summary>
public class MigrationSubjectGrantTests
{
    private const string ApiSecret = "correct-horse-battery-staple";

    private const string ReaderMongoId = "5f1a00000000000000000001";
    private const string AdminMongoId = "5f1a00000000000000000002";
    private const string DeniedMongoId = "5f1a00000000000000000003";

    // Tokens are built the way the source instance built them, from the digest rather than from
    // thin air: DeriveDigest self-checks the reconstruction against the token it was issued with,
    // so an invented token silently yields a null digest and the import stops covering the rule it
    // exists to reproduce.
    private static readonly string ReaderDigest = Digest(ReaderMongoId);
    private static readonly string AdminDigest = Digest(AdminMongoId);
    private static readonly string DeniedDigest = Digest(DeniedMongoId);

    private static readonly string ReaderToken = $"reader-{ReaderDigest[..16]}";
    private static readonly string AdminToken = $"boss-{AdminDigest[..16]}";
    private static readonly string DeniedToken = $"gone-{DeniedDigest[..16]}";

    private static string Digest(string mongoId) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes(MigrationJob.HashApiSecret(ApiSecret) + mongoId)));

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
                        "_id": "{{ReaderMongoId}}",
                        "name": "Reader",
                        "roles": ["readable", "logger"],
                        "accessToken": "{{ReaderToken}}"
                      },
                      {
                        "_id": "{{AdminMongoId}}",
                        "name": "Boss",
                        "roles": ["admin"],
                        "accessToken": "{{AdminToken}}"
                      },
                      {
                        "_id": "{{DeniedMongoId}}",
                        "name": "Gone",
                        "roles": ["denied"],
                        "accessToken": "{{DeniedToken}}"
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

    private static ServiceProvider BuildProvider()
    {
        // Named once, outside the options lambda: the lambda runs per context, so generating the
        // name there would give every context its own store.
        var database = $"migration-grants-{Guid.NewGuid():N}";

        return new ServiceCollection()
            .AddDbContext<NocturneDbContext>(o => o.UseInMemoryDatabase(database))
            // The grant lookup builds its own context, as it does in the request pipeline.
            .AddDbContextFactory<NocturneDbContext>(
                o => o.UseInMemoryDatabase(database), lifetime: ServiceLifetime.Scoped)
            .AddScoped<ITenantAccessor, FixedTenantAccessor>()
            .AddScoped<IAuditContext, AuditContext>()
            .AddSingleton<IHttpClientFactory, StubHttpClientFactory>()
            .BuildServiceProvider();
    }

    /// <summary>
    /// Gives <paramref name="tenantId"/> the owner every import needs somebody to issue tokens to.
    /// </summary>
    private static async Task<Guid> SeedOwnerAsync(IServiceProvider provider, Guid tenantId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var ownerSubjectId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();

        db.Subjects.Add(new SubjectEntity
        {
            Id = ownerSubjectId,
            Name = "Owner",
            Username = "owner",
            IsActive = true,
            ApprovalStatus = "Approved",
        });
        // With a passkey, so the owner is somebody who can sign in rather than another orphan.
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = ownerSubjectId,
            CredentialId = [1],
            PublicKey = [2],
            SignCount = 0,
        });
        db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = roleId,
            TenantId = tenantId,
            Name = "Owner",
            Slug = RoleSeeds.Owner,
        });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = memberId,
            TenantId = tenantId,
            SubjectId = ownerSubjectId,
            DirectPermissions = [Scope.FullAccess],
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            TenantMemberId = memberId,
            TenantRoleId = roleId,
        });

        await db.SaveChangesAsync();
        return ownerSubjectId;
    }

    /// <summary>
    /// Runs a subjects-only API migration to completion against <see cref="NightscoutStub"/> and
    /// returns the tenant it imported into.
    /// </summary>
    private static async Task<Guid> RunSubjectMigrationAsync(
        IServiceProvider provider, bool seedOwner = true)
    {
        var tenant = new TenantContext(
            Guid.CreateVersion7(), "migrated", "Migrated Tenant", true, IsDemo: false);

        if (seedOwner)
        {
            await SeedOwnerAsync(provider, tenant.TenantId);
        }

        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = "https://example-nightscout.invalid",
            NightscoutApiSecret = ApiSecret,
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

    private static async Task<OAuthGrantEntity?> GrantForAsync(
        NocturneDbContext db, Guid tenantId, string label) =>
        await db.OAuthGrants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(g => g.TenantId == tenantId && g.Label == label);

    [Fact]
    public async Task Imported_token_becomes_a_grant_holding_exactly_its_Nightscout_permissions()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var grant = await GrantForAsync(db, tenantId, "Reader");

        // "readable" ("*:*:read") carries the reads; the custom "logger" role's "api:treatments:*"
        // is what lets it write treatments back. Asserted exclusively: the risk in translating a
        // legacy grant is granting more than the source did, which a containment check would miss.
        grant!.Scopes.Should().BeEquivalentTo([
            Scope.GlucoseRead,
            Scope.TreatmentsRead,
            Scope.TreatmentsReadWrite,
            Scope.DevicesRead,
            Scope.TherapyRead,
            Scope.FoodRead,
            Scope.AlertsRead,
            Scope.ReportsRead,
            Scope.IdentityRead,
            Scope.HeartRateRead,
            Scope.StepCountRead,
            Scope.SleepRead,
        ]);
    }

    [Fact]
    public async Task Imported_token_leaves_the_tenant_with_nobody_locked_out()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // The regression this whole shape exists to prevent. A device imported as an account is a
        // member with no passkey and no provider, which takes the tenant into recovery mode for
        // every request and cannot be recovered from the UI.
        (await db.OrphanedSubjectsOf(tenantId).ToListAsync()).Should().BeEmpty();

        // The only accounts are the owner and the device holder, which is a system subject.
        db.Subjects.Should().HaveCount(2);
        db.Subjects.Should().ContainSingle(s => s.IsSystemSubject && s.Name == "Devices");
    }

    [Fact]
    public async Task Imported_tokens_are_held_by_a_subject_that_is_nobody()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var grant = await GrantForAsync(db, tenantId, "Reader");
        var holder = await db.Subjects.SingleAsync(s => s.Id == grant!.SubjectId);

        // Issuing to a person makes the token inherit what that person is, which on a self-hosted
        // instance means the owner's platform-admin standing over every tenant.
        holder.IsSystemSubject.Should().BeTrue();
        holder.IsPlatformAdmin.Should().BeFalse();
        holder.Name.Should().Be("Devices");
    }

    [Fact]
    public async Task Imported_grant_resolves_its_scopes_when_the_token_authenticates()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // The other half of the round trip: what MemberScopeMiddleware makes of the grant when the
        // imported token authenticates. A direct grant is a scoped credential, so the holder's full
        // access is the ceiling and the grant's own scopes are what it gets.
        var grant = await GrantForAsync(db, tenantId, "Reader");
        var resolved = MemberScopeResolver.Resolve(
            new HashSet<string> { Scope.FullAccess },
            AuthType.DirectGrant,
            grant!.Scopes.ToHashSet());

        resolved.Should().Contain([Scope.GlucoseRead, Scope.TreatmentsReadWrite]);
        resolved.Should().NotContain([
            Scope.FullAccess, Scope.MembersManage, Scope.SharingManage]);
    }

    [Fact]
    public async Task An_imported_token_is_stored_verbatim_and_as_a_digest()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var grant = await GrantForAsync(db, tenantId, "Reader");

        // Existing AAPS and xDrip setups keep uploading without being re-paired: the hash matches
        // the token the source issued, and the digest covers every other prefix Nightscout's own
        // rule would have accepted.
        grant!.TokenHash.Should().Be(Sha256Hex(ReaderToken));
        grant.LegacyTokenDigest.Should().Be(ReaderDigest);
        grant.IsMigrated.Should().BeTrue();
    }

    [Fact]
    public async Task An_imported_token_authenticates_through_the_grant_lookup()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        // The chain the compatibility claim rests on, end to end: a real import writes the digest,
        // and the token the source issued still resolves through the rule that reads it. Asserting
        // the stored column alone would pass even if nothing could authenticate with it.
        var byIssuedToken = await DirectGrantTokenHandler.FindActiveGrantAsync(
            factory, ReaderToken, tenantId, DateTime.UtcNow);

        byIssuedToken.Should().NotBeNull();
        byIssuedToken!.Label.Should().Be("Reader");

        // The name in front of the dash is cosmetic, and a longer prefix of the same digest is the
        // same credential; both are Nightscout's rule, not ours.
        (await DirectGrantTokenHandler.FindActiveGrantAsync(
            factory, $"anything-{ReaderDigest}", tenantId, DateTime.UtcNow))
            .Should().NotBeNull();

        // A prefix taken from the middle of the digest is not a prefix of it.
        (await DirectGrantTokenHandler.FindActiveGrantAsync(
            factory, $"reader-{ReaderDigest[8..24]}", tenantId, DateTime.UtcNow))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_Nightscout_admin_is_imported_as_a_full_access_grant()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var grant = await GrantForAsync(db, tenantId, "Boss");

        // Stored as the bare atom rather than the expansion, so the grant tracks the scope list.
        grant!.Scopes.Should().Equal(Scope.FullAccess);
    }

    [Fact]
    public async Task A_denied_subject_gets_no_grant()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // "denied" is how Nightscout spells "no access". Importing a working credential for it
        // would hand back access the source instance had taken away.
        (await GrantForAsync(db, tenantId, "Gone")).Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_with_no_owner_still_imports()
    {
        await using var provider = BuildProvider();
        var tenantId = await RunSubjectMigrationAsync(provider, seedOwner: false);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // The holder is created on demand and is not a person, so an import during first-run setup
        // is not blocked on somebody having claimed the site yet.
        (await db.OAuthGrants.IgnoreQueryFilters()
            .CountAsync(g => g.TenantId == tenantId)).Should().Be(2);
    }

    [Fact]
    public async Task A_source_role_does_not_inherit_a_same_named_local_roles_permissions()
    {
        await using var provider = BuildProvider();

        // The instance already has a role called "logger" that grants everything. The source's
        // "logger" is a narrow custom role; the name collision must not widen the import.
        using (var seedScope = provider.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            seed.Roles.Add(new RoleEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "logger",
                Description = "Local role that happens to share the name",
                Permissions = ["*"],
                IsSystemRole = false,
            });
            await seed.SaveChangesAsync();
        }

        var tenantId = await RunSubjectMigrationAsync(provider);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var grant = await GrantForAsync(db, tenantId, "Reader");

        grant!.Scopes.Should().NotContain(Scope.FullAccess);
        grant.Scopes.Should().Contain(Scope.TreatmentsReadWrite);
    }

    [Fact]
    public async Task A_token_already_on_a_grant_is_not_imported_twice()
    {
        await using var provider = BuildProvider();

        var tenant = new TenantContext(
            Guid.CreateVersion7(), "migrated", "Migrated Tenant", true, IsDemo: false);
        var ownerSubjectId = await SeedOwnerAsync(provider, tenant.TenantId);

        using (var seedScope = provider.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            seed.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.TenantId,
                SubjectId = ownerSubjectId,
                GrantType = OAuthGrantTypes.Direct,
                Scopes = [Scope.GlucoseRead],
                Label = "Reader (already here)",
                TokenHash = Sha256Hex(ReaderToken),
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = "https://example-nightscout.invalid",
            NightscoutApiSecret = ApiSecret,
            Collections = ["subjects"],
        };

        var job = new MigrationJob(
            Guid.CreateVersion7(), tenant.TenantId, request,
            new MigrationJobInfo
            {
                Id = Guid.CreateVersion7(),
                Mode = MigrationMode.Api,
                CreatedAt = DateTime.UtcNow,
            },
            tenant, NullLogger.Instance, provider);

        await job.ExecuteAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // Re-running an import must not hand out a second credential for a token the tenant already
        // holds, or revoking the one on the list would leave a working duplicate behind.
        (await db.OAuthGrants.IgnoreQueryFilters()
            .CountAsync(g => g.TenantId == tenant.TenantId && g.TokenHash == Sha256Hex(ReaderToken)))
            .Should().Be(1);
    }

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
