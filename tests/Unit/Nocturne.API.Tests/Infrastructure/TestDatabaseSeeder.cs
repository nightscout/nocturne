using System.Security.Cryptography;
using System.Text;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Seeds a minimal but complete test database so the full ASP.NET middleware pipeline
/// (tenant resolution, setup check, authentication, authorization) works correctly.
/// </summary>
public static class TestDatabaseSeeder
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid TestSubjectId = Guid.Parse("00000000-0000-0000-0000-000000000099");
    public static readonly Guid PublicSubjectId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    /// <summary>
    /// Seeds the database with all entities required for the middleware pipeline:
    /// - Default tenant (with optional API secret hash)
    /// - Test subject with passkey credential (satisfies TenantSetupMiddleware)
    /// - Public system subject with "readable" role (enables unauthenticated GET access)
    /// - Owner role for the test subject
    /// </summary>
    public static void Seed(NocturneDbContext db, string? apiSecretHash = null)
    {
        if (db.Tenants.Any())
            return;

        // 1. Default tenant
        db.Tenants.Add(new TenantEntity
        {
            Id = TenantId,
            Slug = "default",
            DisplayName = "Default",
            IsActive = true,
        });

        // 2. Test subject (human user with passkey — satisfies TenantSetupMiddleware)
        db.Subjects.Add(new SubjectEntity
        {
            Id = TestSubjectId,
            Name = "Test User",
            Username = "test",
            IsActive = true,
            IsSystemSubject = false,
        });

        var testMemberId = Guid.NewGuid();
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = testMemberId,
            TenantId = TenantId,
            SubjectId = TestSubjectId,
        });

        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.NewGuid(),
            SubjectId = TestSubjectId,
            CredentialId = [1, 2, 3, 4],
            PublicKey = [5, 6, 7, 8],
            SignCount = 0,
        });

        // 3. Public system subject (enables unauthenticated read access via PublicAccessCacheService)
        db.Subjects.Add(new SubjectEntity
        {
            Id = PublicSubjectId,
            Name = "Public",
            IsActive = true,
            IsSystemSubject = true,
        });

        var publicMemberId = Guid.NewGuid();
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = publicMemberId,
            TenantId = TenantId,
            SubjectId = PublicSubjectId,
            LimitTo24Hours = true,
        });

        // 4. Roles: "readable" for public, "owner" for test user
        var readableRoleId = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = readableRoleId,
            TenantId = TenantId,
            Name = "Readable",
            Slug = TenantPermissions.SeedRoles.Readable,
            Permissions = TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Readable],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

        var ownerRoleId = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = ownerRoleId,
            TenantId = TenantId,
            Name = "Owner",
            Slug = TenantPermissions.SeedRoles.Owner,
            Permissions = TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

        // 5. If an API secret hash is provided, create a DirectGrant with LegacySecretHash
        //    so ApiKeyHandler can resolve it (replaces the old TenantEntity.ApiSecretHash lookup)
        if (apiSecretHash != null)
        {
            db.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                SubjectId = TestSubjectId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = apiSecretHash,
                Scopes = [OAuthScopes.FullAccess],
                Label = "Legacy API Secret",
                CreatedAt = DateTime.UtcNow,
            });
        }

        // 6. Assign roles
        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.NewGuid(),
            TenantMemberId = publicMemberId,
            TenantRoleId = readableRoleId,
            SysCreatedAt = DateTime.UtcNow,
        });

        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.NewGuid(),
            TenantMemberId = testMemberId,
            TenantRoleId = ownerRoleId,
            SysCreatedAt = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    public static string Sha1Hex(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
