using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.DevOnly;
using Nocturne.API.Models.DevOnly;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.DevOnly;

public class DevAdminScopedSnapshotImportTests : IDisposable
{
    private static readonly Guid SourceTenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid OwnerRoleId = Guid.CreateVersion7();
    private static readonly Guid ViewerRoleId = Guid.CreateVersion7();
    private static readonly Guid MemberId = Guid.CreateVersion7();
    private static readonly Guid OAuthClientId = Guid.CreateVersion7();
    private static readonly Guid ConnectorId = Guid.CreateVersion7();

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _database;

    public DevAdminScopedSnapshotImportTests()
    {
        _database = TestDbContextFactory.CreateSqlite()
            .SeedTenant(_tenantA, "tenant-a")
            .SeedTenant(_tenantB, "tenant-b");
    }

    [Fact]
    public async Task ImportScopedSnapshot_SameSnapshotIntoTwoTenants_ReKeysRowsAndKeepsReferences()
    {
        (await ImportInto(_tenantA)).Should().BeOfType<OkObjectResult>();
        (await ImportInto(_tenantB)).Should().BeOfType<OkObjectResult>();

        var a = await ReadTenant(_tenantA);
        var b = await ReadTenant(_tenantB);

        foreach (var t in new[] { a, b })
        {
            t.RoleIds.Should().HaveCount(2);
            t.MemberIds.Should().ContainSingle();
            t.MemberRoleLinks.Should().HaveCount(2);
            t.MemberRoleLinks.Select(l => l.MemberId).Should().AllBeEquivalentTo(t.MemberIds.Single());
            t.MemberRoleLinks.Select(l => l.RoleId).Should().BeEquivalentTo(t.RoleIds,
                "each link is re-pointed at the importing tenant's own copy of the role");
            t.MemberSubjectIds.Should().Equal(SubjectId);
            t.OAuthClientIds.Should().ContainSingle();
            t.ConnectorIds.Should().ContainSingle();
            t.MemberInviteIds.Should().Equal([null],
                "the snapshot's invite does not exist in the importing tenant");
        }

        var snapshotIds = new[] { OwnerRoleId, ViewerRoleId, MemberId, OAuthClientId, ConnectorId };
        var idsA = a.AllIds().ToList();
        var idsB = b.AllIds().ToList();
        idsA.Should().NotIntersectWith(idsB);
        idsA.Should().NotIntersectWith(snapshotIds);
        idsB.Should().NotIntersectWith(snapshotIds);

        await using var verify = _database.CreateContext();
        (await verify.Subjects.CountAsync(s => s.Id == SubjectId)).Should().Be(1);
    }

    [Fact]
    public async Task ImportScopedSnapshot_Reimport_ReplacesTheTenantsRowsWithoutDuplicating()
    {
        (await ImportInto(_tenantA)).Should().BeOfType<OkObjectResult>();
        (await ImportInto(_tenantA)).Should().BeOfType<OkObjectResult>();

        var a = await ReadTenant(_tenantA);
        a.RoleIds.Should().HaveCount(2);
        a.MemberIds.Should().ContainSingle();
        a.MemberRoleLinks.Should().HaveCount(2);
    }

    [Fact]
    public async Task ImportScopedSnapshot_ReimportIntoSameTenant_RepointsPendingInviteRoleIds()
    {
        (await ImportInto(_tenantA)).Should().BeOfType<OkObjectResult>();
        var firstImport = await ReadRoleIdsBySlug(_tenantA);
        var inviteId = await SeedInvite(_tenantA, [firstImport["owner"], firstImport["viewer"]]);

        (await ImportInto(_tenantA)).Should().BeOfType<OkObjectResult>();

        var secondImport = await ReadRoleIdsBySlug(_tenantA);
        secondImport.Values.Should().NotIntersectWith(firstImport.Values);
        await using var db = _database.CreateContext(_tenantA);
        var invite = await db.MemberInvites.SingleAsync(i => i.Id == inviteId);
        invite.RoleIds.Should().Equal(secondImport["owner"], secondImport["viewer"]);
    }

    [Fact]
    public async Task ImportScopedSnapshot_MemberFromInviteThatExistsInTargetTenant_KeepsInviteLink()
    {
        (await ImportInto(_tenantA)).Should().BeOfType<OkObjectResult>();
        var inviteId = await SeedInvite(_tenantA, [OwnerRoleId]);

        (await ImportInto(_tenantA, Snapshot(inviteId))).Should().BeOfType<OkObjectResult>();

        await using var db = _database.CreateContext(_tenantA);
        var member = await db.TenantMembers.SingleAsync(m => m.TenantId == _tenantA);
        member.CreatedFromInviteId.Should().Be(inviteId);
    }

    [Fact]
    public async Task ImportScopedSnapshot_MemberRoleLinkToAbsentRole_IsRejected()
    {
        var snapshot = Snapshot();
        snapshot.MemberRoles.Add(new TenantMemberRoleEntityDto
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = MemberId,
            TenantRoleId = Guid.CreateVersion7(),
        });

        await using var context = _database.CreateContext();
        var result = await NewController(context).ImportScopedSnapshot(_tenantA, snapshot, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    private async Task<ActionResult> ImportInto(Guid tenantId, TenantSnapshotDto? snapshot = null)
    {
        await using var context = _database.CreateContext();
        return await NewController(context)
            .ImportScopedSnapshot(tenantId, snapshot ?? Snapshot(), CancellationToken.None);
    }

    private async Task<Guid> SeedInvite(Guid tenantId, List<Guid> roleIds)
    {
        var inviteId = Guid.CreateVersion7();
        await using var db = _database.CreateContext(tenantId);
        db.MemberInvites.Add(new()
        {
            Id = inviteId, TenantId = tenantId, CreatedBySubjectId = SubjectId,
            TokenHash = inviteId.ToString("N"), RoleIds = roleIds,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        return inviteId;
    }

    private async Task<Dictionary<string, Guid>> ReadRoleIdsBySlug(Guid tenantId)
    {
        await using var db = _database.CreateContext(tenantId);
        return await db.TenantRoles.Where(r => r.TenantId == tenantId).ToDictionaryAsync(r => r.Slug, r => r.Id);
    }

    private async Task<TenantRows> ReadTenant(Guid tenantId)
    {
        await using var db = _database.CreateContext(tenantId);
        var roleIds = await db.TenantRoles.Where(r => r.TenantId == tenantId).Select(r => r.Id).ToListAsync();
        var members = await db.TenantMembers.Where(m => m.TenantId == tenantId).ToListAsync();
        var memberIds = members.Select(m => m.Id).ToList();
        var links = await db.TenantMemberRoles
            .Where(mr => memberIds.Contains(mr.TenantMemberId))
            .Select(mr => new MemberRoleLink(mr.Id, mr.TenantMemberId, mr.TenantRoleId))
            .ToListAsync();
        var clientIds = await db.OAuthClients.Where(c => c.TenantId == tenantId).Select(c => c.Id).ToListAsync();
        var connectorIds = await db.ConnectorConfigurations
            .Where(c => c.TenantId == tenantId).Select(c => c.Id).ToListAsync();

        return new TenantRows(
            roleIds, memberIds, members.Select(m => m.SubjectId).ToList(),
            members.Select(m => m.CreatedFromInviteId).ToList(), links, clientIds, connectorIds);
    }

    private static TenantSnapshotDto Snapshot(Guid? createdFromInviteId = null) =>
        new()
        {
            Tenant = new TenantEntityDto { Id = SourceTenantId, Slug = "source", DisplayName = "Source" },
            Subjects = [new SubjectEntityDto { Id = SubjectId, Name = "owner", IsActive = true }],
            Roles =
            [
                new TenantRoleEntityDto
                {
                    Id = OwnerRoleId, TenantId = SourceTenantId, Name = "Owner", Slug = "owner",
                    Permissions = ["*"], IsSystem = true,
                },
                new TenantRoleEntityDto
                {
                    Id = ViewerRoleId, TenantId = SourceTenantId, Name = "Viewer", Slug = "viewer",
                    Permissions = ["glucose.read"], IsSystem = true,
                },
            ],
            Members =
            [
                new TenantMemberEntityDto
                {
                    Id = MemberId, TenantId = SourceTenantId, SubjectId = SubjectId,
                    CreatedFromInviteId = createdFromInviteId ?? Guid.CreateVersion7(),
                },
            ],
            MemberRoles =
            [
                new TenantMemberRoleEntityDto
                {
                    Id = Guid.CreateVersion7(), TenantMemberId = MemberId, TenantRoleId = OwnerRoleId,
                },
                new TenantMemberRoleEntityDto
                {
                    Id = Guid.CreateVersion7(), TenantMemberId = MemberId, TenantRoleId = ViewerRoleId,
                },
            ],
            OAuthClients =
            [
                new OAuthClientEntityDto { Id = OAuthClientId, TenantId = SourceTenantId, ClientId = "client" },
            ],
            ConnectorConfigurations =
            [
                new ConnectorConfigSnapshotDto { Id = ConnectorId, TenantId = SourceTenantId, ConnectorName = "Dexcom" },
            ],
        };

    private static DevAdminController NewController(NocturneDbContext context) =>
        new(
            context,
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<IConnectorSyncService>(),
            Mock.Of<ITenantAccessor>(),
            Mock.Of<ITenantService>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DevAdminController>.Instance);

    private sealed record MemberRoleLink(Guid Id, Guid MemberId, Guid RoleId);

    private sealed record TenantRows(
        List<Guid> RoleIds,
        List<Guid> MemberIds,
        List<Guid> MemberSubjectIds,
        List<Guid?> MemberInviteIds,
        List<MemberRoleLink> MemberRoleLinks,
        List<Guid> OAuthClientIds,
        List<Guid> ConnectorIds)
    {
        public IEnumerable<Guid> AllIds() =>
            RoleIds.Concat(MemberIds).Concat(MemberRoleLinks.Select(l => l.Id))
                .Concat(OAuthClientIds).Concat(ConnectorIds);
    }

    public void Dispose() => _database.Dispose();
}
