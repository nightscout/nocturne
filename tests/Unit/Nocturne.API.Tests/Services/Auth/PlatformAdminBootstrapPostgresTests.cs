using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The startup bootstrap's owner choice and its replacement of platform admins who cannot sign in,
/// against real queries on Postgres under the runtime role. Each test takes a database of its own,
/// because the bootstrap reads every tenant on the instance.
/// </summary>
[Trait("Category", "Integration")]
public class PlatformAdminBootstrapPostgresTests(PlatformAdminBootstrapPostgresTests.Databases databases)
    : IClassFixture<PlatformAdminBootstrapPostgresTests.Databases>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Hands each test a migrated database of its own.</summary>
    public sealed class Databases
    {
        public Task<TestDatabase> CreateAsync() =>
            SharedPostgres.CreateMigratedDatabaseAsync("platform_admin_bootstrap");
    }

    private sealed class Instance(TestDatabase database, bool linkRoute)
    {
        public NocturneDbContext Context(Guid tenant) =>
            TestContexts.Context(database.AppConnectionString, tenant);

        private SuppliedContextFactory Contexts(Guid tenant) => new(() => Context(tenant));

        public IAccountLinkRoute Route => AccountLinkRoutes.Of(linkRoute);

        public SubjectRepository Subjects => new(Contexts(Guid.Empty), Route);

        public TenantMembershipRepository Members(Guid tenant) => new(Contexts(tenant), Route);

        public async Task<Guid> AddTenantAsync(string slug, DateTime createdAt)
        {
            var tenant = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
            {
                await conn.OpenAsync(Ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                    VALUES (@id, @slug, 'bootstrap-test', true, @created, @created)
                    """;
                cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
                cmd.Parameters.Add(new NpgsqlParameter("@slug", slug));
                cmd.Parameters.Add(new NpgsqlParameter("@created", createdAt));
                await cmd.ExecuteNonQueryAsync(Ct);
            }

            await Members(tenant).SeedRolesAsync(tenant, Ct);
            return tenant;
        }

        public async Task<Guid> AddOwnerAsync(Guid tenant, string name, bool signsIn = true)
        {
            var subject = await Subjects.CreateAsync(new NewSubject(name), Ct);
            await MakeOwnerAsync(tenant, subject);
            if (signsIn)
                await AddPasskeyAsync(subject);
            return subject;
        }

        public async Task MakeOwnerAsync(Guid tenant, Guid subject)
        {
            var members = Members(tenant);
            var owner = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
            (await members.AddMemberAsync(
                    new NewTenantMember(tenant, subject, [owner]) { How = MembershipJoinHow.Direct, By = null }, Ct))
                .Should().BeTrue();
        }

        public async Task<Guid> AddAdminAsync(string name, bool signsIn = false)
        {
            var subject = await Subjects.CreateAsync(new NewSubject(name), Ct);
            await Subjects.GrantPlatformAdminAsync([subject], Ct);
            if (signsIn)
                await AddPasskeyAsync(subject);
            return subject;
        }

        public async Task AddPasskeyAsync(Guid subject)
        {
            await using var db = Context(Guid.Empty);
            db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subject,
                CredentialId = subject.ToByteArray(),
                PublicKey = [1],
            });
            await db.SaveChangesAsync(Ct);
        }

        public async Task ConfirmEmailAsync(Guid subject)
        {
            await using var db = Context(Guid.Empty);
            var entity = await db.Subjects.SingleAsync(s => s.Id == subject, Ct);
            var address = $"{subject:N}@example.test";
            entity.SetEmail(address);
            entity.ConfirmEmail(address, DateTime.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        public async Task GrantAdminAsync(Guid subject) =>
            (await Subjects.GrantPlatformAdminAsync([subject], Ct)).Should().Be(1);

        public async Task AddIdentityAsync(Guid subject, bool providerEnabled)
        {
            await using var db = Context(Guid.Empty);
            var provider = Guid.CreateVersion7();
            var issuer = $"https://{provider:N}.idp.example.test";
            db.OidcProviders.Add(new OidcProviderEntity
            {
                Id = provider, Name = "Provider", IssuerUrl = issuer, ClientId = "client", IsEnabled = providerEnabled,
            });
            db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = subject, ProviderId = provider,
                OidcSubjectId = $"ext-{subject:N}", Issuer = issuer, LinkedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        public async Task AddLoginCodeAsync(
            Guid tenant, Guid subject, LoginCodePurpose purpose, DateTime expiresAt, DateTime? consumedAt = null)
        {
            await using var db = Context(tenant);
            db.LoginCodes.Add(new LoginCodeEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject,
                CodeHash = Guid.NewGuid().ToString("N"), Purpose = purpose,
                ExpiresAt = expiresAt, ConsumedAt = consumedAt,
            });
            await db.SaveChangesAsync(Ct);
        }

        public async Task DeactivateAsync(Guid subject)
        {
            await using var db = Context(Guid.Empty);
            var entity = await db.Subjects.SingleAsync(s => s.Id == subject, Ct);
            entity.IsActive = false;
            await db.SaveChangesAsync(Ct);
        }

        public async Task<bool> IsAdminAsync(Guid subject)
        {
            await using var db = Context(Guid.Empty);
            return await db.Subjects.AsNoTracking().Where(s => s.Id == subject).Select(s => s.IsPlatformAdmin).SingleAsync(Ct);
        }

        public PlatformAdminBootstrapService Service(List<Guid>? configured = null) =>
            new(
                new TenantRepository(Contexts(Guid.Empty)),
                Members(Guid.Empty),
                Subjects,
                Options.Create(new PlatformOptions { AdminSubjectIds = configured ?? [] }),
                NullLogger<PlatformAdminBootstrapService>.Instance);
    }

    private async Task<Instance> NewInstanceAsync(bool linkRoute = false) =>
        new(await databases.CreateAsync(), linkRoute);

    [Fact]
    public async Task AnOwnerWhoCannotSignInIsSkippedForTheNextOldestOwnerWhoCan()
    {
        var instance = await NewInstanceAsync();
        var oldest = await instance.AddTenantAsync("oldest", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = await instance.AddTenantAsync("newer", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var stranded = await instance.AddOwnerAsync(oldest, "stranded", signsIn: false);
        var capable = await instance.AddOwnerAsync(newer, "capable");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(stranded)).Should().BeFalse();
        (await instance.IsAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task AnOwnerWhoCannotSignInIsSkippedForAnotherOwnerOfTheSameTenant()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("shared", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var stranded = await instance.AddOwnerAsync(tenant, "stranded", signsIn: false);
        var capable = await instance.AddOwnerAsync(tenant, "capable");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(stranded)).Should().BeFalse();
        (await instance.IsAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenNoOwnerCanSignIn_NobodyIsGranted()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var stranded = await instance.AddOwnerAsync(tenant, "stranded", signsIn: false);

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(stranded)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AConfirmedAddressCountsAsSigningInOnlyWhileALinkRouteIsConfigured(bool linkRoute, bool granted)
    {
        var instance = await NewInstanceAsync(linkRoute);
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var owner = await instance.AddOwnerAsync(tenant, "owner", signsIn: false);
        await instance.ConfirmEmailAsync(owner);

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(owner)).Should().Be(granted);
    }

    [Fact]
    public async Task WhenEveryAdminCannotSignIn_TheFlagMovesToTheOwnerWhoCan()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var strandedOwner = await instance.AddOwnerAsync(tenant, "stranded-owner", signsIn: false);
        var capable = await instance.AddOwnerAsync(tenant, "capable");
        var strandedAdmin = await instance.AddAdminAsync("stranded-admin");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(strandedAdmin)).Should().BeFalse();
        (await instance.IsAdminAsync(strandedOwner)).Should().BeFalse();
        (await instance.IsAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenEveryAdminCannotSignInAndNoOwnerCan_TheAdminIsKept()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var strandedOwner = await instance.AddOwnerAsync(tenant, "stranded-owner", signsIn: false);
        var strandedAdmin = await instance.AddAdminAsync("stranded-admin");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(strandedAdmin)).Should().BeTrue();
        (await instance.IsAdminAsync(strandedOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAdminWhoCanSignInIsNeverCleared_EvenBesideOneWhoCannot()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var capableOwner = await instance.AddOwnerAsync(tenant, "capable-owner");
        var capableAdmin = await instance.AddAdminAsync("capable-admin", signsIn: true);
        var strandedAdmin = await instance.AddAdminAsync("stranded-admin");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(capableAdmin)).Should().BeTrue();
        (await instance.IsAdminAsync(strandedAdmin)).Should().BeTrue();
        (await instance.IsAdminAsync(capableOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task ConfiguredAdminSubjectIdsMeanNoHeal()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var capableOwner = await instance.AddOwnerAsync(tenant, "capable-owner");
        var strandedAdmin = await instance.AddAdminAsync("stranded-admin");
        var configured = await instance.Subjects.CreateAsync(new NewSubject("configured"), Ct);

        await instance.Service([configured]).BootstrapAsync(Ct);

        (await instance.IsAdminAsync(strandedAdmin)).Should().BeTrue();
        (await instance.IsAdminAsync(configured)).Should().BeTrue();
        (await instance.IsAdminAsync(capableOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task AfterAnAbandonedOwnerStepAndARestart_AnotherSubjectCompletingSetupBecomesPlatformAdmin()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var leftover = await instance.AddOwnerAsync(tenant, "leftover", signsIn: false);

        await instance.Service().BootstrapAsync(Ct);
        (await instance.IsAdminAsync(leftover)).Should().BeFalse();

        var completing = await instance.AddOwnerAsync(tenant, "completing");

        (await instance.Service().EnsureFirstOwnerIsPlatformAdminAsync(completing, Ct)).Should().BeTrue();
        (await instance.IsAdminAsync(completing)).Should().BeTrue();
        (await instance.IsAdminAsync(leftover)).Should().BeFalse();
    }

    [Fact]
    public async Task ALeftoverOwnerAlreadyHoldingTheFlag_LosesItToTheOwnerCompletingSetupWithoutARestart()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var leftover = await instance.AddOwnerAsync(tenant, "leftover", signsIn: false);
        await instance.GrantAdminAsync(leftover);

        await instance.Service().BootstrapAsync(Ct);
        (await instance.IsAdminAsync(leftover)).Should().BeTrue();

        var completing = await instance.AddOwnerAsync(tenant, "completing");

        (await instance.Service().EnsureFirstOwnerIsPlatformAdminAsync(completing, Ct)).Should().BeTrue();
        (await instance.IsAdminAsync(completing)).Should().BeTrue();
        (await instance.IsAdminAsync(leftover)).Should().BeFalse();
    }

    [Fact]
    public async Task SetupCompletionNeverTakesTheFlagFromAnAdminWhoCanSignIn()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var admin = await instance.AddOwnerAsync(tenant, "admin");
        await instance.GrantAdminAsync(admin);
        var completing = await instance.AddOwnerAsync(tenant, "completing");

        (await instance.Service().EnsureFirstOwnerIsPlatformAdminAsync(completing, Ct)).Should().BeFalse();
        (await instance.IsAdminAsync(admin)).Should().BeTrue();
        (await instance.IsAdminAsync(completing)).Should().BeFalse();
    }

    [Fact]
    public async Task ALeftoverOwnerHoldingTheFlag_LosesItAtStartupToAnOwnerWhoCanSignIn()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var leftover = await instance.AddOwnerAsync(tenant, "leftover", signsIn: false);
        await instance.GrantAdminAsync(leftover);
        var capable = await instance.AddOwnerAsync(tenant, "capable");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(leftover)).Should().BeFalse();
        (await instance.IsAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task AnAdminWhoseOnlyFactorIsAnIdentityOnADisabledProvider_KeepsTheFlag()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var capable = await instance.AddOwnerAsync(tenant, "capable");
        var admin = await instance.AddAdminAsync("admin");
        await instance.AddIdentityAsync(admin, providerEnabled: false);

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(admin)).Should().BeTrue();
        (await instance.IsAdminAsync(capable)).Should().BeFalse();
    }

    [Theory]
    [InlineData(LoginCodePurpose.Recovery)]
    [InlineData(LoginCodePurpose.OperatorRecovery)]
    [InlineData(LoginCodePurpose.EmailRecovery)]
    [InlineData(LoginCodePurpose.RecoverySession)]
    public async Task AnAdminHoldingAPendingRecoveryCode_KeepsTheFlag(LoginCodePurpose purpose)
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var admin = await instance.AddOwnerAsync(tenant, "admin", signsIn: false);
        await instance.GrantAdminAsync(admin);
        var capable = await instance.AddOwnerAsync(tenant, "capable");
        await instance.AddLoginCodeAsync(tenant, admin, purpose, DateTime.UtcNow.AddHours(1));

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(admin)).Should().BeTrue();
        (await instance.IsAdminAsync(capable)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAdminHoldingAPendingRecoveryCodeOnlyOnALaterTenant_KeepsTheFlag()
    {
        var instance = await NewInstanceAsync();
        var first = await instance.AddTenantAsync("first", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var later = await instance.AddTenantAsync("later", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var admin = await instance.AddOwnerAsync(first, "admin", signsIn: false);
        await instance.MakeOwnerAsync(later, admin);
        await instance.GrantAdminAsync(admin);
        var capable = await instance.AddOwnerAsync(first, "capable");
        await instance.AddLoginCodeAsync(later, admin, LoginCodePurpose.Recovery, DateTime.UtcNow.AddHours(1));

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(admin)).Should().BeTrue();
        (await instance.IsAdminAsync(capable)).Should().BeFalse();
    }

    [Theory]
    [InlineData(LoginCodePurpose.Recovery, true, false)]
    [InlineData(LoginCodePurpose.Recovery, false, true)]
    [InlineData(LoginCodePurpose.Login, false, false)]
    public async Task AnExpiredOrRedeemedCodeOrALoginCode_DoesNotKeepTheFlag(
        LoginCodePurpose purpose, bool expired, bool consumed)
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var admin = await instance.AddOwnerAsync(tenant, "admin", signsIn: false);
        await instance.GrantAdminAsync(admin);
        var capable = await instance.AddOwnerAsync(tenant, "capable");
        await instance.AddLoginCodeAsync(
            tenant, admin, purpose,
            expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            consumed ? DateTime.UtcNow.AddMinutes(-5) : null);

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(admin)).Should().BeFalse();
        (await instance.IsAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task ADeactivatedAdmin_LosesTheFlagToAnOwnerWhoCanSignIn()
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var capable = await instance.AddOwnerAsync(tenant, "capable");
        var admin = await instance.AddAdminAsync("admin", signsIn: true);
        await instance.DeactivateAsync(admin);

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(admin)).Should().BeFalse();
        (await instance.IsAdminAsync(capable)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task AnIdentityCountsForTheSuccessorOnlyWhileItsProviderIsEnabled(bool providerEnabled, bool moves)
    {
        var instance = await NewInstanceAsync();
        var tenant = await instance.AddTenantAsync("only", new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var owner = await instance.AddOwnerAsync(tenant, "owner", signsIn: false);
        await instance.AddIdentityAsync(owner, providerEnabled);
        var admin = await instance.AddAdminAsync("admin");

        await instance.Service().BootstrapAsync(Ct);

        (await instance.IsAdminAsync(owner)).Should().Be(moves);
        (await instance.IsAdminAsync(admin)).Should().Be(!moves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingWithASuccessorWhoCannotSignIn_ChangesNothing(bool disabledIdentity)
    {
        var instance = await NewInstanceAsync();
        var admin = await instance.AddAdminAsync("admin");
        var successor = await instance.Subjects.CreateAsync(new NewSubject("successor"), Ct);
        if (disabledIdentity)
            await instance.AddIdentityAsync(successor, providerEnabled: false);

        (await instance.Subjects.ReplaceStrandedPlatformAdminsAsync(successor, Ct)).Should().BeEmpty();

        (await instance.IsAdminAsync(admin)).Should().BeTrue();
        (await instance.IsAdminAsync(successor)).Should().BeFalse();
    }

    [Fact]
    public async Task ReplacingWithASuccessorWhoCanSignIn_MovesTheFlag()
    {
        var instance = await NewInstanceAsync();
        var admin = await instance.AddAdminAsync("admin");
        var successor = await instance.Subjects.CreateAsync(new NewSubject("successor"), Ct);
        await instance.AddPasskeyAsync(successor);

        (await instance.Subjects.ReplaceStrandedPlatformAdminsAsync(successor, Ct)).Should().Equal(admin);

        (await instance.IsAdminAsync(admin)).Should().BeFalse();
        (await instance.IsAdminAsync(successor)).Should().BeTrue();
    }
}
