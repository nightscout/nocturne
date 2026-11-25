using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A subject only a team row names is not found unclaimed, on PostgreSQL under the runtime role, where
/// <c>team_tenant_access</c> shows a context pinned to a tenant only that tenant's rows and the team
/// tables show a context pinned to no subject every row.
/// </summary>
[Trait("Category", "Integration")]
public class SubjectUnclaimedTeamRowsPostgresTests(SubjectUnclaimedTeamRowsPostgresTests.Database database)
    : IClassFixture<SubjectUnclaimedTeamRowsPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("unclaimed_team_rows");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static NocturneDbContext Context(string connectionString, Guid tenant) =>
        TestContexts.Context(connectionString, tenant);

    private static string Issuer(Guid provider) => $"https://{provider:N}.example";

    private sealed record Scene(SubjectRepository Subjects, Guid Provider, Guid Team, Guid Taker, Guid Elsewhere);

    private async Task<Scene> SceneAsync()
    {
        var home = await TenantAsync("home");
        var elsewhere = await TenantAsync("elsewhere");
        var subjects = new SubjectRepository(
            new SuppliedContextFactory(() => Context(database.AppConnectionString, home)), AccountLinkRoutes.Off);
        var provider = Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        var taker = await subjects.CreateAsync(new NewSubject("taker"), Ct);
        var creator = await subjects.CreateAsync(new NewSubject("creator"), Ct);
        await using var db = Context(database.MigratorConnectionString, Guid.Empty);
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = provider, Name = "Keycloak", IssuerUrl = Issuer(provider), ClientId = "nocturne", IsEnabled = true,
        });
        db.Teams.Add(new TeamEntity { Id = team, Name = "Team", CreatedBySubjectId = creator });
        await db.SaveChangesAsync(Ct);
        return new Scene(subjects, provider, team, taker, elsewhere);
    }

    private async Task<Guid> TenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using var db = Context(database.MigratorConnectionString, Guid.Empty);
        db.Tenants.Add(new TenantEntity { Id = tenant, Slug = $"{slug}-{tenant:N}", DisplayName = slug, IsActive = true });
        await db.SaveChangesAsync(Ct);
        return tenant;
    }

    private async Task<(Guid Subject, Guid Identity)> HolderAsync(Scene scene, string name, Action<NocturneDbContext, Guid>? nameIt = null)
    {
        var subject = await scene.Subjects.CreateAsync(new NewSubject(name), Ct);
        var identity = await scene.Subjects.AddOidcIdentityAsync(
            new NewSubjectOidcIdentity(
                subject, scene.Provider, $"ext-{subject:N}", Issuer(scene.Provider), null, DateTime.UtcNow), Ct);
        if (nameIt is not null)
        {
            await using var db = Context(database.MigratorConnectionString, Guid.Empty);
            nameIt(db, subject);
            await db.SaveChangesAsync(Ct);
        }

        return (subject, identity);
    }

    private async Task<bool> ExistsAsync(Guid subject)
    {
        await using var db = Context(database.MigratorConnectionString, Guid.Empty);
        return await db.Subjects.AnyAsync(s => s.Id == subject, Ct);
    }

    [Fact]
    public async Task A_holder_nothing_names_is_deleted()
    {
        var scene = await SceneAsync();
        var (holder, identity) = await HolderAsync(scene, "bare");

        (await scene.Subjects.TakeUnclaimedOidcIdentityAsync(identity, scene.Taker, null, Ct)).Should().NotBeNull();

        (await ExistsAsync(holder)).Should().BeFalse();
    }

    [Fact]
    public async Task A_holder_a_team_member_row_names_is_kept()
    {
        var scene = await SceneAsync();
        var (holder, identity) = await HolderAsync(scene, "member", (db, subject) =>
            db.TeamMembers.Add(new TeamMemberEntity { Id = Guid.CreateVersion7(), TeamId = scene.Team, SubjectId = subject }));

        (await scene.Subjects.TakeUnclaimedOidcIdentityAsync(identity, scene.Taker, null, Ct)).Should().BeNull();

        (await ExistsAsync(holder)).Should().BeTrue();
    }

    [Fact]
    public async Task A_holder_who_made_a_staff_invite_is_kept()
    {
        var scene = await SceneAsync();
        var (holder, identity) = await HolderAsync(scene, "staff-invite-maker", (db, subject) =>
            db.TeamStaffInvites.Add(new TeamStaffInviteEntity
            {
                Id = Guid.CreateVersion7(), TeamId = scene.Team, CreatedBySubjectId = subject,
                TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(1),
            }));

        (await scene.Subjects.TakeUnclaimedOidcIdentityAsync(identity, scene.Taker, null, Ct)).Should().BeNull();

        (await ExistsAsync(holder)).Should().BeTrue();
    }

    [Fact]
    public async Task A_holder_who_approved_a_teams_access_to_another_tenant_is_kept()
    {
        var scene = await SceneAsync();
        var (holder, identity) = await HolderAsync(scene, "approver", (db, subject) =>
            db.TeamTenantAccess.Add(new TeamTenantAccessEntity
            {
                Id = Guid.CreateVersion7(), TeamId = scene.Team, TenantId = scene.Elsewhere, ApprovedBySubjectId = subject,
            }));

        (await scene.Subjects.TakeUnclaimedOidcIdentityAsync(identity, scene.Taker, null, Ct)).Should().BeNull();

        (await ExistsAsync(holder)).Should().BeTrue();
        await using var db = Context(database.MigratorConnectionString, Guid.Empty);
        (await db.TeamTenantAccess.SingleAsync(a => a.TenantId == scene.Elsewhere, Ct)).ApprovedBySubjectId
            .Should().Be(holder, "the refusal leaves the approval as it was");
    }
}
