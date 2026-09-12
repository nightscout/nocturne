using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>MoveSubjectTokensToDirectGrants</c> over seeded rows on a real PostgreSQL.
/// </summary>
/// <remarks>
/// The sibling RLS suite is deliberately seedless, so on an empty database this migration's
/// <c>DO</c> block is syntax-checked and nothing more: its loop iterates zero tenants. A migration
/// that moves credentials needs a test that moves credentials.
/// <para>
/// Everything is asserted in one run against one seed, because the fixture cost is a container plus
/// the full migration chain and the cases do not interact: each tenant below is independent.
/// </para>
/// </remarks>
public class SubjectTokenConversionFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20260908125351_AddDedupReconcileCursorLinkId";

    private const string DbName = "nocturne_token_conversion";
    private const string MigratorPassword = "token-conversion-migrator-password";

    private PostgreSqlContainer _container = null!;
    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid Converts = Guid.Parse("11111111-1111-7111-8111-111111111111");
    internal static readonly Guid Clamped = Guid.Parse("22222222-2222-7222-8222-222222222222");
    internal static readonly Guid LeftAlone = Guid.Parse("33333333-3333-7333-8333-333333333333");

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17.6")
            .WithDatabase(DbName)
            .WithUsername("postgres")
            .WithPassword("bootstrap-test-password")
            .WithEnvironment("NOCTURNE_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("NOCTURNE_APP_PASSWORD", "token-conversion-app-password")
            .WithEnvironment("NOCTURNE_WEB_PASSWORD", "token-conversion-web-password")
            .WithBindMount(ResolveInitScriptPath(), "/docker-entrypoint-initdb.d/00-init.sh")
            .Build();

        await _container.StartAsync();

        _migratorConnectionString =
            $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};"
            + $"Database={DbName};Username=nocturne_migrator;Password={MigratorPassword}";

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(targetMigration: null);
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Migrates to <paramref name="targetMigration"/>, or to head when it is null. Seeding has to
    /// happen between two states of the schema, which <c>RunMigrationsAsync</c> cannot express.
    /// </summary>
    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    internal async Task<T?> ScalarAsync<T>(Guid tenantId, string sql)
    {
        await using var conn = await OpenAsync();

        await using (var pin = conn.CreateCommand())
        {
            pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
            pin.Parameters.AddWithValue("tenant", tenantId.ToString());
            await pin.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>
    /// One tenant per behaviour under test. Written as raw SQL against the pre-migration schema,
    /// because the entity model no longer has the columns the old rows lived in.
    /// </summary>
    private async Task SeedAsync()
    {
        await ExecuteAsync($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{Converts}',  'converts',  'Converts',  true, now(), now()),
              ('{Clamped}',   'clamped',   'Clamped',   true, now(), now()),
              ('{LeftAlone}', 'leftalone', 'LeftAlone', true, now(), now());

            -- Converts: an owner who can sign in, plus one device per shape the migration decides on.
            -- Carries a token as well as a passkey, so the passkey guard is the only thing keeping
            -- this row out of the device set. Without one the guard could be deleted unnoticed.
            INSERT INTO subjects (id, name, username, access_token_hash, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('11111111-0000-7000-8000-00000000a001', 'Owner', 'owner', repeat('9',64), true, false, now(), now(), 'Approved');
            INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
            VALUES (gen_random_uuid(), '11111111-0000-7000-8000-00000000a001', 'c'::bytea, 'p'::bytea, 0, ARRAY[]::text[], now());

            INSERT INTO subjects (id, name, access_token_hash, legacy_token_digest, is_active, is_system_subject, created_at, updated_at, approval_status) VALUES
              ('11111111-0000-7000-8000-00000000d001', 'Uploader',  repeat('1',64), repeat('a',40), true,  false, now(), now(), 'Approved'),
              ('11111111-0000-7000-8000-00000000d002', 'RoleOnly',  repeat('2',64), NULL,           true,  false, now(), now(), 'Approved'),
              ('11111111-0000-7000-8000-00000000d003', 'Scalar',    repeat('3',64), NULL,           true,  false, now(), now(), 'Approved'),
              ('11111111-0000-7000-8000-00000000d004', 'Parked',    repeat('4',64), NULL,           false, false, now(), now(), 'Approved');

            -- A demo tenant is rebuilt on a schedule and its visitor stands for nobody, so neither
            -- is this migration's business.
            INSERT INTO subjects (id, name, access_token_hash, is_active, is_system_subject, is_demo_subject, created_at, updated_at, approval_status)
            VALUES ('11111111-0000-7000-8000-00000000d005', 'DemoDevice', repeat('6',64), true, false, true, now(), now(), 'Approved');

            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours) VALUES
              ('11111111-0000-7000-8000-00000000b001', '{Converts}', '11111111-0000-7000-8000-00000000a001', '["*"]'::jsonb, now(), now(), false),
              ('11111111-0000-7000-8000-00000000b002', '{Converts}', '11111111-0000-7000-8000-00000000d001', '["glucose.read"]'::jsonb, now(), now(), false),
              ('11111111-0000-7000-8000-00000000b003', '{Converts}', '11111111-0000-7000-8000-00000000d002', '[]'::jsonb, now(), now(), false),
              ('11111111-0000-7000-8000-00000000b004', '{Converts}', '11111111-0000-7000-8000-00000000d003', '42'::jsonb, now(), now(), false),
              ('11111111-0000-7000-8000-00000000b005', '{Converts}', '11111111-0000-7000-8000-00000000d004', '["glucose.read"]'::jsonb, now(), now(), false),
              ('11111111-0000-7000-8000-00000000b006', '{Converts}', '11111111-0000-7000-8000-00000000d005', '["glucose.read"]'::jsonb, now(), now(), false);

            -- RoleOnly's authority is a tenant role, not direct permissions.
            INSERT INTO tenant_roles (id, tenant_id, name, slug, permissions, is_system, sys_created_at, sys_updated_at)
            VALUES ('11111111-0000-7000-8000-00000000c001', '{Converts}', 'Uploader', 'uploader', '["treatments.readwrite"]'::jsonb, false, now(), now());
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            VALUES (gen_random_uuid(), '11111111-0000-7000-8000-00000000b003', '11111111-0000-7000-8000-00000000c001');

            -- Clamped: a device its owner restricted to the last 24 hours.
            INSERT INTO subjects (id, name, access_token_hash, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('22222222-0000-7000-8000-00000000d001', 'Follower', repeat('5',64), true, false, now(), now(), 'Approved');
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
            VALUES (gen_random_uuid(), '{Clamped}', '22222222-0000-7000-8000-00000000d001', '["glucose.read"]'::jsonb, now(), now(), true);

            -- LeftAlone: a person who never enrolled. Not a device, and not this migration's problem.
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('33333333-0000-7000-8000-00000000a001', 'Invitee', true, false, now(), now(), 'Approved');
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
            VALUES ('33333333-0000-7000-8000-00000000b001', '{LeftAlone}', '33333333-0000-7000-8000-00000000a001', '["glucose.read"]'::jsonb, now(), now(), false);
            """);
    }

    private static string ResolveInitScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "docs/postgres/container-init/00-init.sh")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException(
                "Could not locate docs/postgres/container-init/00-init.sh from " + AppContext.BaseDirectory)
            : Path.Join(dir.FullName, "docs/postgres/container-init/00-init.sh");
    }
}

/// <inheritdoc cref="SubjectTokenConversionFixture"/>
[Trait("Category", "Integration")]
public class SubjectTokenConversionTests(SubjectTokenConversionFixture fixture)
    : IClassFixture<SubjectTokenConversionFixture>
{
    private static readonly Guid Converts = SubjectTokenConversionFixture.Converts;
    private static readonly Guid Clamped = SubjectTokenConversionFixture.Clamped;
    private static readonly Guid LeftAlone = SubjectTokenConversionFixture.LeftAlone;

    private Task<T?> ScalarAsync<T>(Guid tenantId, string sql) => fixture.ScalarAsync<T>(tenantId, sql);

    [Fact]
    public async Task An_uploader_token_survives_with_its_hash_and_digest()
    {
        var scopes = await ScalarAsync<string>(Converts,
            "SELECT scopes::text FROM oauth_grants WHERE label = 'Uploader';");
        var hash = await ScalarAsync<string>(Converts,
            "SELECT token_hash FROM oauth_grants WHERE label = 'Uploader';");
        var digest = await ScalarAsync<string>(Converts,
            "SELECT legacy_token_digest FROM oauth_grants WHERE label = 'Uploader';");

        scopes.Should().Be("{glucose.read}");
        hash.Should().Be(new string('1', 64));
        digest.Should().Be(new string('a', 40));
    }

    [Fact]
    public async Task A_device_whose_access_came_from_a_role_keeps_it()
    {
        // MemberScopeMiddleware unions role permissions with direct ones, so reading
        // direct_permissions alone would drop this device's entire authority.
        var scopes = await ScalarAsync<string>(Converts,
            "SELECT scopes::text FROM oauth_grants WHERE label = 'RoleOnly';");

        scopes.Should().Be("{treatments.readwrite}");
    }

    [Fact]
    public async Task Malformed_permissions_do_not_abort_the_migration()
    {
        // AND does not short-circuit in SQL, so a jsonb_array_length guarded only by a
        // jsonb_typeof test in the same WHERE clause errors on a scalar and rolls back the whole Up.
        // Migrations run at API startup, so that is a crash loop.
        var grants = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM oauth_grants WHERE label = 'Scalar';");
        var members = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{Converts}' AND s.name = 'Scalar';");

        grants.Should().Be(0, "the row authorizes nothing, so there is no credential to carry over");
        members.Should().Be(0, "and a device is not an account, so the membership goes either way");
    }

    [Fact]
    public async Task A_clamped_devices_limit_is_carried_across()
    {
        // Nothing enforces it for this credential type yet, so this records the operator's intent
        // rather than a restriction; see OAuthGrantEntity.LimitTo24Hours. Dropping it on the way
        // through would lose the intent before the gap is closed.
        var clamped = await ScalarAsync<bool>(Clamped,
            "SELECT limit_to_24_hours FROM oauth_grants WHERE label = 'Follower';");

        clamped.Should().BeTrue();
    }

    [Fact]
    public async Task The_tenant_is_left_with_nobody_locked_out()
    {
        // The whole point: a member with no passkey and no provider answers 503 recovery_mode on
        // every API request, and the recovery page cannot resolve an account with no username.
        // tenant_members and subjects carry no RLS policy, so the tenant has to be named rather
        // than left to the GUC.
        var orphans = await ScalarAsync<long>(Converts,
            $"""
            SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id
             WHERE tm.tenant_id = '{Converts}' AND tm.revoked_at IS NULL AND s.is_active
               AND NOT s.is_system_subject AND NOT s.is_demo_subject
               AND NOT EXISTS (SELECT 1 FROM passkey_credentials p WHERE p.subject_id = s.id)
               AND NOT EXISTS (SELECT 1 FROM subject_oidc_identities i WHERE i.subject_id = s.id);
            """);

        orphans.Should().Be(0);
    }

    [Fact]
    public async Task The_owner_is_untouched()
    {
        // A DELETE keyed on anything wider than the device set takes the owner with it, and a
        // tenant with no member who can sign in has no way back in.
        var owner = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{Converts}' AND s.name = 'Owner';");
        var ownerGrants = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM oauth_grants WHERE label = 'Owner';");

        owner.Should().Be(1);

        // A person who happens to hold a legacy token as well as a passkey is still a person. Their
        // token is not a device credential and must not be reissued as one.
        ownerGrants.Should().Be(0);
    }

    [Fact]
    public async Task A_deactivated_devices_token_is_not_reissued()
    {
        var grants = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM oauth_grants WHERE label = 'Parked';");

        // Deactivating is how a credential is switched off. Converting it would hand back access
        // the instance had deliberately taken away, as a grant nothing has revoked.
        grants.Should().Be(0);
    }

    [Fact]
    public async Task A_deactivated_device_leaves_nobody_locked_out()
    {
        // Its token columns are dropped either way, so keeping the membership preserves nothing and
        // leaves a credential-less member that OrphanedSubjectFilter reports the moment anyone
        // reactivates the subject.
        var members = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{Converts}' AND s.name = 'Parked';");

        members.Should().Be(0);
    }

    [Fact]
    public async Task A_demo_subject_is_left_alone()
    {
        var grants = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM oauth_grants WHERE label = 'DemoDevice';");
        var members = await ScalarAsync<long>(Converts,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{Converts}' AND s.name = 'DemoDevice';");

        grants.Should().Be(0);
        members.Should().Be(1);
    }

    [Fact]
    public async Task A_person_with_no_credentials_is_not_treated_as_a_device()
    {
        // A different bug with a different fix. Sweeping them up here would delete the membership
        // of somebody who is simply waiting to enrol.
        var members = await ScalarAsync<long>(LeftAlone,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{LeftAlone}' AND s.name = 'Invitee';");
        var grants = await ScalarAsync<long>(LeftAlone,
            $"SELECT count(*) FROM oauth_grants WHERE tenant_id = '{LeftAlone}';");

        members.Should().Be(1);
        grants.Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_with_no_tokens_gains_no_holder()
    {
        var subjects = await ScalarAsync<long>(LeftAlone,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{LeftAlone}' AND s.name = 'Devices';");

        subjects.Should().Be(0);
    }

    [Fact]
    public async Task The_holder_is_a_system_subject_who_is_nobody()
    {
        var isSystem = await ScalarAsync<bool>(Converts,
            "SELECT s.is_system_subject FROM subjects s"
            + $" JOIN tenant_members tm ON tm.subject_id = s.id AND tm.tenant_id = '{Converts}'"
            + " WHERE s.name = 'Devices';");
        var isPlatformAdmin = await ScalarAsync<bool>(Converts,
            "SELECT s.is_platform_admin FROM subjects s"
            + $" JOIN tenant_members tm ON tm.subject_id = s.id AND tm.tenant_id = '{Converts}'"
            + " WHERE s.name = 'Devices';");

        isSystem.Should().BeTrue();
        isPlatformAdmin.Should().BeFalse();
    }
}
