using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

public sealed class ShareLinkServiceTests : IDisposable
{
    private static readonly Guid TenantId = TestDatabaseSeeder.TenantId;

    private readonly NocturneDbContext _db;
    private readonly ShareLinkService _service;

    public ShareLinkServiceTests()
    {
        var dbName = $"sharelink_{Guid.NewGuid()}";
        _db = TestDbContextFactory.CreateInMemoryContext(dbName);
        TestDatabaseSeeder.Seed(_db);

        // The seeder grants the Public subject the Clinician role; keep a Viewer role available too
        // so the legacy role-based link scenario can be exercised.
        _db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Name = "Viewer",
            Slug = RoleSeeds.Viewer,
            Permissions = RoleSeeds.Permissions[RoleSeeds.Viewer],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext(dbName));

        _service = new ShareLinkService(
            _db,
            new ShareTokenGenerator(),
            new ShareTokenCacheService(
                new MemoryCache(new MemoryCacheOptions()), factory.Object, NullLogger<ShareTokenCacheService>.Instance),
            new PublicAccessCacheService(
                new MemoryCache(new MemoryCacheOptions()), factory.Object, NullLogger<PublicAccessCacheService>.Instance),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.run" }));
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Loads the Public subject's membership with its role assignments.</summary>
    private Task<TenantMemberEntity> GetPublicMemberAsync() =>
        _db.TenantMembers.AsNoTracking().Include(m => m.MemberRoles)
            .FirstAsync(m => m.TenantId == TenantId && m.Subject!.Name == "Public");

    /// <summary>Strips the seeded role/scope grant so the Public subject mirrors a fresh tenant.</summary>
    private async Task ResetPublicMemberAccessAsync()
    {
        var member = await _db.TenantMembers.Include(m => m.MemberRoles)
            .FirstAsync(m => m.TenantId == TenantId && m.Subject!.Name == "Public");
        _db.TenantMemberRoles.RemoveRange(member.MemberRoles);
        member.MemberRoles.Clear();
        member.DirectPermissions = null;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Rotate_enables_sharing_and_returns_a_url()
    {
        var dto = await _service.RotateAsync(TenantId);

        dto.Enabled.Should().BeTrue();
        dto.Url.Should().MatchRegex(@"^https://[0-9a-z]+\.share\.nocturne\.run$");

        // The URL carries the token; the column carries only its digest.
        var token = new Uri(dto.Url!).Host.Split('.')[0];
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId);
        tenant.ShareToken.Should().Be(CredentialHash.ShareToken(token))
            .And.NotBe(token);
    }

    [Fact]
    public async Task Get_reports_the_link_as_enabled_without_revealing_the_url()
    {
        await _service.RotateAsync(TenantId);

        var dto = await _service.GetAsync(TenantId);

        dto.Enabled.Should().BeTrue();
        dto.Url.Should().BeNull("the token is not stored, so the URL cannot be reproduced");
    }

    [Fact]
    public async Task Rotate_seeds_default_scopes_as_direct_permissions_on_first_enable()
    {
        await ResetPublicMemberAccessAsync();

        var dto = await _service.RotateAsync(TenantId);

        dto.Scopes.Should().BeEquivalentTo(Scope.DefaultPublicShareScopes);

        var member = await GetPublicMemberAsync();
        member.DirectPermissions.Should().BeEquivalentTo(Scope.DefaultPublicShareScopes);
        member.MemberRoles.Should().BeEmpty("rotation seeds direct permissions rather than a role");
    }

    [Fact]
    public async Task Rotate_changes_the_token_each_time()
    {
        var first = (await _service.RotateAsync(TenantId)).Url;
        var second = (await _service.RotateAsync(TenantId)).Url;

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Disable_clears_the_token_roles_and_scopes()
    {
        await _service.RotateAsync(TenantId);

        var dto = await _service.DisableAsync(TenantId);

        dto.Enabled.Should().BeFalse();
        dto.Url.Should().BeNull();
        dto.Scopes.Should().BeEmpty();

        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId);
        tenant.ShareToken.Should().BeNull();

        var member = await GetPublicMemberAsync();
        member.MemberRoles.Should().BeEmpty();
        (member.DirectPermissions ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task SetFullHistory_toggles_the_24_hour_limit()
    {
        await _service.RotateAsync(TenantId); // defaults to 24h

        (await _service.SetFullHistoryAsync(TenantId, fullHistory: true)).FullHistory.Should().BeTrue();
        (await _service.SetFullHistoryAsync(TenantId, fullHistory: false)).FullHistory.Should().BeFalse();
    }

    [Fact]
    public async Task Get_reflects_disabled_state_by_default()
    {
        var dto = await _service.GetAsync(TenantId);

        dto.Enabled.Should().BeFalse();
        dto.Url.Should().BeNull();
    }

    [Fact]
    public async Task Get_reports_role_derived_scopes_for_the_public_member()
    {
        // The seeder grants the Public subject the Clinician role; its read atoms must surface as
        // the current public scopes so legacy (role-based) links keep working.
        var dto = await _service.GetAsync(TenantId);

        dto.Scopes.Should().Contain([
            Scope.GlucoseRead,
            Scope.TreatmentsRead,
        ]);
        dto.Scopes.Should().OnlyContain(s => Scope.PublicShareScopes.Contains(s));
    }

    [Fact]
    public async Task Rerotate_preserves_the_full_history_choice()
    {
        await _service.RotateAsync(TenantId);
        await _service.SetFullHistoryAsync(TenantId, fullHistory: true);

        var dto = await _service.RotateAsync(TenantId);

        dto.FullHistory.Should().BeTrue("re-rotation must not reset the owner's full-history choice");
    }

    [Fact]
    public async Task Rerotate_preserves_the_chosen_scopes()
    {
        await ResetPublicMemberAccessAsync();
        await _service.RotateAsync(TenantId);
        await _service.SetScopesAsync(TenantId, [Scope.GlucoseRead]);

        var dto = await _service.RotateAsync(TenantId);

        dto.Scopes.Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task SetScopes_replaces_direct_permissions_and_drops_role_grants()
    {
        // Start from the seeded Clinician-role link, then choose explicit scopes.
        await _service.RotateAsync(TenantId);

        var dto = await _service.SetScopesAsync(TenantId,
            [Scope.GlucoseRead, Scope.TreatmentsRead]);

        dto.Scopes.Should().BeEquivalentTo([Scope.GlucoseRead, Scope.TreatmentsRead]);

        var member = await GetPublicMemberAsync();
        member.MemberRoles.Should().BeEmpty("choosing scopes migrates the link onto direct permissions");
        member.DirectPermissions.Should().BeEquivalentTo([Scope.GlucoseRead, Scope.TreatmentsRead]);
    }

    [Fact]
    public async Task SetScopes_allows_an_empty_list_while_the_link_stays_live()
    {
        await _service.RotateAsync(TenantId);

        var dto = await _service.SetScopesAsync(TenantId, []);

        dto.Enabled.Should().BeTrue("the link is live via its token, independent of shared scopes");
        dto.Scopes.Should().BeEmpty();

        var member = await GetPublicMemberAsync();
        (member.DirectPermissions ?? []).Should().BeEmpty();
        member.MemberRoles.Should().BeEmpty();
    }

    [Fact]
    public async Task SetScopes_rejects_scopes_outside_the_public_allow_list()
    {
        await _service.RotateAsync(TenantId);

        var setReadWrite = async () => await _service.SetScopesAsync(TenantId, [Scope.GlucoseReadWrite]);
        var setAdmin = async () => await _service.SetScopesAsync(TenantId, [Scope.MembersManage]);

        await setReadWrite.Should().ThrowAsync<ArgumentException>();
        await setAdmin.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Disable_without_a_public_member_still_clears_the_token()
    {
        await _service.RotateAsync(TenantId);

        // Remove the Public membership entirely, then disable.
        var publicMember = await _db.TenantMembers.Include(m => m.MemberRoles)
            .FirstAsync(m => m.TenantId == TenantId && m.Subject!.Name == "Public");
        _db.TenantMemberRoles.RemoveRange(publicMember.MemberRoles);
        _db.TenantMembers.Remove(publicMember);
        await _db.SaveChangesAsync();

        var dto = await _service.DisableAsync(TenantId); // must not throw

        dto.Enabled.Should().BeFalse();
        (await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId)).ShareToken.Should().BeNull();
    }

    /// <summary>Stores display preferences against the seeded owner subject.</summary>
    private async Task GiveTheOwnerPreferencesAsync(UserDisplayPreferences preferences)
    {
        var owner = await _db.Subjects.FirstAsync(s => s.Id == TestDatabaseSeeder.TestSubjectId);
        owner.Preferences = preferences.Serialize();
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds a second owner of the tenant, joining <paramref name="joinedAfter"/> the seeded one.
    /// </summary>
    private Task AddOwnerAsync(
        UserDisplayPreferences preferences,
        DateTime joinedAfter,
        bool isSystemSubject = false,
        bool isActiveSubject = true) =>
        TestDatabaseSeeder.SeedMemberAsync(
            _db, TenantId,
            isActive: isActiveSubject,
            isSystemSubject: isSystemSubject,
            joinedAt: joinedAfter,
            preferences: preferences.Serialize());

    /// <summary>Backdates the seeded owner's membership so later arrivals sort after it.</summary>
    private async Task<DateTime> BackdateTheSeededOwnerAsync()
    {
        var joined = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var member = await _db.TenantMembers
            .FirstAsync(m => m.SubjectId == TestDatabaseSeeder.TestSubjectId);
        member.SysCreatedAt = joined;
        await _db.SaveChangesAsync();
        return joined;
    }

    [Fact]
    public async Task SharedAppearance_is_the_owners_presentation_settings()
    {
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            TimeFormat = "24",
            RegionFormat = "en-GB",
            ColorTheme = "trio",
            Chart = new ChartPreferences { LineColorMode = "threshold", Lookback = 6 },
            Prediction = new PredictionPreferences { Enabled = true, Minutes = 45 },
        });

        var appearance = await _service.GetSharedAppearanceAsync(TenantId);

        appearance.GlucoseUnits.Should().Be("mmol");
        appearance.TimeFormat.Should().Be("24");
        appearance.RegionFormat.Should().Be("en-GB");
        appearance.ColorTheme.Should().Be("trio");
        appearance.Chart!.LineColorMode.Should().Be("threshold");
        appearance.Chart.Lookback.Should().Be(6);
        appearance.Prediction!.Minutes.Should().Be(45);
    }

    [Fact]
    public async Task SharedAppearance_withholds_what_the_owner_does_rather_than_how_it_looks()
    {
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            DashboardTopWidgets = [WidgetId.Tdd, WidgetId.Meals],
            NightModeSchedule = true,
        });

        var appearance = await _service.GetSharedAppearanceAsync(TenantId);

        appearance.GlucoseUnits.Should().Be("mmol");
        appearance.DashboardTopWidgets.Should().BeNull(
            "the widgets an owner pins describe what they track, not how it looks");
        appearance.NightModeSchedule.Should().BeNull(
            "it tells a stranger holding the link roughly when the owner sleeps");
    }

    [Fact]
    public async Task SharedAppearance_is_empty_when_the_tenant_has_no_owner_left()
    {
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences { GlucoseUnits = "mmol" });

        var owner = await _db.TenantMembers.FirstAsync(m => m.SubjectId == TestDatabaseSeeder.TestSubjectId);
        owner.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var appearance = await _service.GetSharedAppearanceAsync(TenantId);

        appearance.GlucoseUnits.Should().BeNull("a revoked member no longer speaks for the tenant");
    }

    [Fact]
    public async Task SharedAppearance_ignores_another_tenants_owner()
    {
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences { GlucoseUnits = "mmol" });

        var appearance = await _service.GetSharedAppearanceAsync(Guid.NewGuid());

        appearance.GlucoseUnits.Should().BeNull();
    }

    [Fact]
    public async Task SharedAppearance_settles_on_the_longest_standing_of_several_owners()
    {
        var seededJoined = await BackdateTheSeededOwnerAsync();
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences { GlucoseUnits = "mmol" });
        await AddOwnerAsync(
            new UserDisplayPreferences { GlucoseUnits = "mg/dl" }, seededJoined.AddYears(1));

        var appearance = await _service.GetSharedAppearanceAsync(TenantId);

        appearance.GlucoseUnits.Should().Be("mmol", "a later co-owner does not restyle the link");
    }

    [Fact]
    public async Task SharedAppearance_ignores_a_system_subject_holding_the_owner_role()
    {
        var seededJoined = await BackdateTheSeededOwnerAsync();
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences { GlucoseUnits = "mmol" });
        await AddOwnerAsync(
            new UserDisplayPreferences { GlucoseUnits = "mg/dl" },
            seededJoined.AddYears(-1),
            isSystemSubject: true);

        var appearance = await _service.GetSharedAppearanceAsync(TenantId);

        appearance.GlucoseUnits.Should().Be("mmol", "no person is behind a system subject");
    }

    [Fact]
    public async Task SharedAppearance_ignores_an_owner_whose_subject_is_deactivated()
    {
        var seededJoined = await BackdateTheSeededOwnerAsync();
        await GiveTheOwnerPreferencesAsync(new UserDisplayPreferences { GlucoseUnits = "mmol" });
        await AddOwnerAsync(
            new UserDisplayPreferences { GlucoseUnits = "mg/dl" },
            seededJoined.AddYears(-1),
            isActiveSubject: false);

        var appearance = await _service.GetSharedAppearanceAsync(TenantId);

        appearance.GlucoseUnits.Should().Be("mmol", "a deactivated subject cannot sign in either");
    }

}
