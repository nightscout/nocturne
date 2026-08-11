using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Demo;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Demo;

/// <summary>
/// Tests that a demo reset clears configuration as well as data while keeping the
/// tenant's identity, so cached tenant contexts and the public share link survive it.
/// </summary>
public class DemoTenantServiceResetTests : IDisposable
{
    private const string DemoSlug = "demo";
    private const string ShareToken = "sharetoken123";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly DemoTenantService _service;

    public DemoTenantServiceResetTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions);
        seed.Database.EnsureCreated();

        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));

        // Stand in for the real re-seed, which recreates the seed roles and the Public
        // membership that ConfigureAccessAsync then grants on top.
        _tenantService
            .Setup(t => t.SeedAfterResetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid tenantId, CancellationToken _) => SeedRolesAndPublicMemberAsync(tenantId));

        _service = new DemoTenantService(
            dbFactory.Object,
            _tenantService.Object,
            TestPublicAccessCache.Create(),
            new Mock<ICacheService>().Object,
            new Mock<ILogger<DemoTenantService>>().Object);
    }

    [Fact]
    public async Task ResetAsync_PreservesTenantIdentity()
    {
        var tenantId = SeedDemoTenant();

        var result = await _service.ResetAsync();

        result.Should().Be(tenantId);

        await using var db = new NocturneDbContext(_dbOptions);
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.Slug.Should().Be(DemoSlug);
        tenant.ShareToken.Should().Be(ShareToken, "share links must keep resolving across a reset");
        tenant.IsDemo.Should().BeTrue();
        tenant.OnboardingCompletedAt.Should().NotBeNull("a demo visitor must not be sent to /setup");
    }

    [Fact]
    public async Task ResetAsync_CarriesDemoScheduleAcross()
    {
        var tenantId = SeedDemoTenant();

        await _service.ResetAsync();

        await using var db = new NocturneDbContext(_dbOptions);
        var config = await db.Set<TenantDemoConfigEntity>().SingleAsync(c => c.TenantId == tenantId);
        config.ResetIntervalMinutes.Should().Be(1440);
        config.BackfillDays.Should().Be(90);
        config.LastResetAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResetAsync_ClearsVisitorConfigurationChanges()
    {
        var tenantId = SeedDemoTenant();
        SeedVisitorChanges(tenantId);

        await _service.ResetAsync();

        await using var db = new NocturneDbContext(_dbOptions);
        db.TenantId = tenantId;

        (await db.AlertRules.CountAsync()).Should().Be(0, "alert rules are configuration and must be reset");
        (await db.TrackerDefinitions.CountAsync()).Should().Be(0, "tracker definitions must be reset");
        (await db.Foods.CountAsync()).Should().Be(0, "the food catalog must be reset");
        (await db.SensorGlucose.CountAsync()).Should().Be(0, "generated data must be reset");
    }

    [Fact]
    public async Task ResetAsync_ReinstatesDemoMemberAndPublicAccess()
    {
        var tenantId = SeedDemoTenant();
        SeedVisitorChanges(tenantId);

        await _service.ResetAsync();

        var subjectId = await _service.FindDemoMemberSubjectIdAsync(tenantId);
        subjectId.Should().NotBeNull("visitors are signed in as the demo member after a reset");

        await using var db = new NocturneDbContext(_dbOptions);
        db.TenantId = tenantId;

        var members = await db.TenantMembers
            .Include(m => m.Subject)
            .Include(m => m.MemberRoles)
            .Where(m => m.TenantId == tenantId)
            .ToListAsync();

        members.Should().HaveCount(2, "only the Public subject and the demo member remain");

        var publicMember = members.Single(m => m.Subject!.IsSystemSubject);
        publicMember.LimitTo24Hours.Should().BeFalse();
        publicMember.DirectPermissions.Should().BeEquivalentTo(
            TenantPermissions.PublicShareScopes,
            "the share link shows every shareable category, and nothing beyond that vocabulary");
        publicMember.MemberRoles.Should().BeEmpty(
            "the Public subject must not hold the demo member's write and administration atoms");

        var demoMember = members.Single(m => !m.Subject!.IsSystemSubject);
        demoMember.Username.Should().Be(DemoTenantService.DemoMemberUsername);
        demoMember.MemberRoles.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConfigureAccessAsync_StripsAPreExistingRoleFromThePublicMembership()
    {
        // The reset path wipes the tenant first, so the cascade removes any stale role for
        // free. POST /provision on an already-provisioned tenant does NOT wipe — it calls
        // ConfigureAccessAsync alone — and that is the state production is in, because the
        // code this branch replaced assigned the seed admin role to the Public membership.
        // Effective permissions union roles with direct permissions, so leaving that row
        // would keep tenant.settings and members.manage on the anonymous share viewer and
        // make writing DirectPermissions pointless.
        var tenantId = SeedDemoTenant();

        // Put the tenant back into the state the replaced code left it in: the Public
        // membership carrying the seed admin role.
        await using (var db = new NocturneDbContext(_dbOptions))
        {
            db.TenantId = tenantId;
            var publicMemberId = await db.TenantMembers
                .Where(m => m.TenantId == tenantId && m.Subject!.IsSystemSubject)
                .Select(m => m.Id)
                .SingleAsync();
            var adminRoleId = await db.TenantRoles
                .Where(r => r.TenantId == tenantId && r.Slug == TenantPermissions.SeedRoles.Admin)
                .Select(r => r.Id)
                .SingleAsync();

            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = publicMemberId,
                TenantRoleId = adminRoleId,
                SysCreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await _service.ConfigureAccessAsync(tenantId);

        await using (var db = new NocturneDbContext(_dbOptions))
        {
            db.TenantId = tenantId;
            var publicMember = await db.TenantMembers
                .Include(m => m.MemberRoles)
                    .ThenInclude(mr => mr.TenantRole)
                .Include(m => m.Subject)
                .SingleAsync(m => m.TenantId == tenantId && m.Subject!.IsSystemSubject);

            publicMember.MemberRoles.Should().BeEmpty(
                "provisioning rewrites the Public grant from source, so a stale admin role must go");
            publicMember.DirectPermissions.Should().BeEquivalentTo(TenantPermissions.PublicShareScopes);
        }
    }

    [Fact]
    public async Task ConfigureAccessAsync_DoesNotBindDemoMemberToAnUnrelatedSubjectNamedDemo()
    {
        // subjects.username carries no unique index and any operator or invitee can pick
        // one, so resolving the demo member by global username could hand an anonymous
        // caller a session for someone else's account.
        Guid impostorId;
        using (var db = new NocturneDbContext(_dbOptions))
        {
            var impostor = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "Real Operator",
                Username = DemoTenantService.DemoMemberUsername,
                IsActive = true,
                IsPlatformAdmin = true,
            };
            db.Subjects.Add(impostor);
            db.SaveChanges();
            impostorId = impostor.Id;
        }

        var tenantId = SeedDemoTenant();

        var subjectId = await _service.FindDemoMemberSubjectIdAsync(tenantId);
        subjectId.Should().NotBeNull().And.NotBe(impostorId);

        await using var check = new NocturneDbContext(_dbOptions);
        var impostorAfter = await check.Subjects.SingleAsync(s => s.Id == impostorId);
        impostorAfter.IsPlatformAdmin.Should().BeTrue("the demo must not touch an unrelated account");
        (await check.TenantMembers.AnyAsync(m => m.SubjectId == impostorId))
            .Should().BeFalse("the impostor must not become a member of the demo tenant");
    }

    [Fact]
    public async Task ResetAsync_RevokesDemoVisitorSessions()
    {
        var tenantId = SeedDemoTenant();
        var subjectId = (await _service.FindDemoMemberSubjectIdAsync(tenantId))!.Value;

        using (var db = new NocturneDbContext(_dbOptions))
        {
            db.RefreshTokens.Add(new RefreshTokenEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                TokenHash = "visitor-token",
                IssuedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                IpAddress = "203.0.113.7",
            });
            db.SaveChanges();
        }

        await _service.ResetAsync();

        await using var check = new NocturneDbContext(_dbOptions);
        (await check.RefreshTokens.CountAsync()).Should().Be(
            0, "a reset must not leave visitor sessions or their recorded IPs behind");
    }

    [Fact]
    public async Task ConfigureAccessAsync_MarksTheDemoMemberAsADemoSubject()
    {
        var tenantId = SeedDemoTenant();

        var subjectId = (await _service.FindDemoMemberSubjectIdAsync(tenantId))!.Value;

        await using var db = new NocturneDbContext(_dbOptions);
        var subject = await db.Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsDemoSubject.Should().BeTrue(
            "anyone can obtain a session for it, so endpoints that assume a real user must refuse it");
    }

    /// <summary>
    /// The lookup is what the anonymous session endpoint decides on, so holding the membership
    /// under the demo username must not be enough on its own.
    /// </summary>
    /// <remarks>
    /// Provisioning adopts a pre-existing membership under that username rather than asserting it
    /// created the subject behind it, and nothing downstream of the lookup re-examines whose subject
    /// it is — so without the flag check here a real account holding that membership would be handed
    /// out as a session.
    /// </remarks>
    [Fact]
    public async Task FindDemoMemberSubjectIdAsync_IgnoresAMembershipHeldByANonDemoSubject()
    {
        var tenantId = SeedDemoTenant();
        var subjectId = (await _service.FindDemoMemberSubjectIdAsync(tenantId))!.Value;

        // Mutation of the state, not of the assertion: the membership is untouched and still
        // matches on username, so only the flag can be what makes this fail to resolve.
        await using (var db = new NocturneDbContext(_dbOptions))
        {
            var subject = await db.Subjects.SingleAsync(s => s.Id == subjectId);
            subject.IsDemoSubject = false;
            await db.SaveChangesAsync();
        }

        (await _service.FindDemoMemberSubjectIdAsync(tenantId)).Should().BeNull(
            "a subject that is not flagged as the demo account must not be resolvable as one");
    }

    [Fact]
    public async Task ResetAsync_RemovesMembershipsTheDemoSubjectAcquiredElsewhere()
    {
        // A visitor could accept a leaked invite to a real tenant. Publicly-obtainable
        // credentials must not survive the reset against that tenant.
        var tenantId = SeedDemoTenant();
        var subjectId = (await _service.FindDemoMemberSubjectIdAsync(tenantId))!.Value;

        var otherTenantId = Guid.CreateVersion7();
        using (var db = new NocturneDbContext(_dbOptions))
        {
            db.Add(new TenantEntity
            {
                Id = otherTenantId,
                Slug = "alice",
                DisplayName = "Alice",
                IsActive = true,
            });
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = otherTenantId,
                SubjectId = subjectId,
            });
            db.SaveChanges();
        }

        await _service.ResetAsync();

        await using var check = new NocturneDbContext(_dbOptions);
        (await check.TenantMembers.AnyAsync(m => m.SubjectId == subjectId))
            .Should().BeFalse("the retired demo subject must hold no memberships anywhere");
        (await check.Subjects.AnyAsync(s => s.Id == subjectId))
            .Should().BeFalse("and the subject itself must be gone");
        (await check.Tenants.AnyAsync(t => t.Id == otherTenantId))
            .Should().BeTrue("the unrelated tenant must survive untouched");
    }

    [Fact]
    public async Task ResetAsync_ReturnsNull_WhenNoDemoTenantExists()
    {
        var result = await _service.ResetAsync();

        result.Should().BeNull();
        _tenantService.Verify(
            t => t.SeedAfterResetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Seeds a provisioned demo tenant: roles, Public membership, demo member, demo config.</summary>
    private Guid SeedDemoTenant()
    {
        using var db = new NocturneDbContext(_dbOptions);

        var tenant = new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = DemoSlug,
            DisplayName = "Nocturne Demo",
            IsActive = true,
            IsDemo = true,
            ShareToken = ShareToken,
            ShareTokenSetAt = DateTime.UtcNow,
            OnboardingCompletedAt = DateTime.UtcNow,
        };
        db.Add(tenant);
        db.Set<TenantDemoConfigEntity>().Add(new TenantDemoConfigEntity
        {
            TenantId = tenant.Id,
            ResetIntervalMinutes = 1440,
            BackfillDays = 90,
            IntervalMinutes = 5,
        });
        db.SaveChanges();

        SeedRolesAndPublicMemberAsync(tenant.Id).GetAwaiter().GetResult();
        _service.ConfigureAccessAsync(tenant.Id).GetAwaiter().GetResult();

        return tenant.Id;
    }

    /// <summary>Configuration and data a visitor could leave behind, all of which a reset must clear.</summary>
    private void SeedVisitorChanges(Guid tenantId)
    {
        using var db = new NocturneDbContext(_dbOptions);
        db.TenantId = tenantId;

        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = "Visitor rule",
        });
        db.TrackerDefinitions.Add(new TrackerDefinitionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = "Visitor tracker",
        });
        db.Foods.Add(new FoodEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = "Visitor food",
        });
        db.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Timestamp = DateTime.UtcNow,
            Mgdl = 120,
        });

        // A member the visitor invited, plus its subject.
        var invited = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "Invited",
            Username = "invited",
            IsActive = true,
        };
        db.Subjects.Add(invited);
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = invited.Id,
        });

        db.SaveChanges();
    }

    /// <summary>
    /// Mirrors the seed-roles + Public-membership half of provisioning, and reproduces the
    /// state an already-provisioned demo tenant is in: the code this branch replaced assigned
    /// the seed <c>admin</c> role to the Public membership, so production carries one and
    /// provisioning has to remove it. Without that role here, the assertion that the Public
    /// membership ends with none would only pin "does not add one".
    /// </summary>
    private async Task SeedRolesAndPublicMemberAsync(Guid tenantId)
    {
        await using var db = new NocturneDbContext(_dbOptions);

        var roles = new Dictionary<string, Guid>();
        foreach (var (slug, permissions) in TenantPermissions.SeedRolePermissions)
        {
            var roleId = Guid.CreateVersion7();
            roles[slug] = roleId;
            db.TenantRoles.Add(new TenantRoleEntity
            {
                Id = roleId,
                TenantId = tenantId,
                Name = TenantPermissions.SeedRoleNames[slug],
                Slug = slug,
                Permissions = new List<string>(permissions),
                IsSystem = true,
            });
        }

        var publicSubject = await db.Subjects
            .FirstOrDefaultAsync(s => s.IsSystemSubject && s.Name == "Public");

        if (publicSubject is null)
        {
            publicSubject = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "Public",
                IsSystemSubject = true,
                IsActive = true,
            };
            db.Subjects.Add(publicSubject);
        }

        var publicMemberId = Guid.CreateVersion7();
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = publicMemberId,
            TenantId = tenantId,
            SubjectId = publicSubject.Id,
            LimitTo24Hours = true,
            Label = "Public Access",
        });

        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = publicMemberId,
            TenantRoleId = roles[TenantPermissions.SeedRoles.Admin],
            SysCreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
