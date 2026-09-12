using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// A tenant answers 503 recovery_mode for as long as this reports anybody, so a subject it names
/// wrongly takes the whole tenant offline and one it misses leaves somebody locked out with no way
/// back in.
/// </summary>
[Trait("Category", "Unit")]
public class OrphanedSubjectFilterTests : IDisposable
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public OrphanedSubjectFilterTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
        _context = _db.CreateContext(_tenant);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedMember(
        string name,
        Guid? tenantId = null,
        bool isActive = true,
        bool isSystemSubject = false,
        bool isDemoSubject = false,
        bool withPasskey = false,
        bool withOidc = false,
        DateTime? revokedAt = null)
    {
        var subjectId = Guid.CreateVersion7();
        _context.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = name,
            IsActive = isActive,
            IsSystemSubject = isSystemSubject,
            IsDemoSubject = isDemoSubject,
            ApprovalStatus = "Approved",
        });
        _context.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId ?? _tenant,
            SubjectId = subjectId,
            RevokedAt = revokedAt,
        });

        if (withPasskey)
        {
            _context.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                CredentialId = [1],
                PublicKey = [2],
                SignCount = 0,
            });
        }

        if (withOidc)
        {
            var providerId = Guid.CreateVersion7();
            _context.OidcProviders.Add(new OidcProviderEntity
            {
                Id = providerId,
                Name = "Test provider",
                IssuerUrl = "https://idp.invalid",
                ClientId = "client",
            });
            _context.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                ProviderId = providerId,
                Issuer = "https://idp.invalid",
                OidcSubjectId = $"ext-{subjectId:N}",
            });
        }

        _context.SaveChanges();
        return subjectId;
    }

    private async Task<List<string>> OrphansAsync() =>
        await _context.OrphanedSubjectsOf(_tenant).Select(s => s.Name).ToListAsync();

    [Fact]
    public async Task A_member_with_no_passkey_and_no_provider_is_locked_out()
    {
        SeedMember("Stranded");

        (await OrphansAsync()).Should().ContainSingle().Which.Should().Be("Stranded");
    }

    [Fact]
    public async Task A_demo_visitor_is_not_locked_out()
    {
        // Credential-less by design and standing for nobody. TenantSetupMiddleware bypasses demo
        // tenants before it ever asks, so reporting it here is only harmless until somebody asks
        // without that bypass in front of them.
        SeedMember("Demo Visitor", isDemoSubject: true);

        (await OrphansAsync()).Should().BeEmpty();
    }

    [Theory]
    // The Public subject a share link runs as: a system row, not a person.
    [InlineData(true, false, false, false)]
    // Deactivated, so not somebody waiting to get back in.
    [InlineData(false, true, false, false)]
    // Can sign in, by either factor.
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task A_subject_who_is_not_a_locked_out_person_is_not_reported(
        bool isSystemSubject, bool deactivated, bool withPasskey, bool withOidc)
    {
        SeedMember(
            "Not an orphan",
            isActive: !deactivated,
            isSystemSubject: isSystemSubject,
            withPasskey: withPasskey,
            withOidc: withOidc);

        (await OrphansAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_membership_does_not_count()
    {
        // Revoking is how a member is removed, so the account is no longer this tenant's problem.
        SeedMember("Departed", revokedAt: DateTime.UtcNow);

        (await OrphansAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_locked_out_member_does_not_brick_this_one()
    {
        SeedMember("Elsewhere", tenantId: _otherTenant);

        (await OrphansAsync()).Should().BeEmpty();
    }
}
