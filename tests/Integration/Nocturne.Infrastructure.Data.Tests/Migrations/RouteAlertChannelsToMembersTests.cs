using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RouteAlertChannelsToMembers</c> over seeded alert channels on a real PostgreSQL.
/// </summary>
/// <remarks>
/// One run against one seed, as in <see cref="MigratedSecretHolderFixture"/>. <see cref="Ownerless"/>
/// counts <see cref="Owner"/> as a member who holds no role there, so a channel it leaves blank
/// shows one tenant's owner is never borrowed for another.
/// </remarks>
public class RouteAlertChannelsToMembersFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261021070559_UnverifyKnownClientsWithForeignRedirectUris";

    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid Routes = Guid.Parse("77777777-7777-7777-8777-777777777777");
    internal static readonly Guid Ownerless = Guid.Parse("88888888-8888-7888-8888-888888888888");

    internal static readonly Guid Owner = Guid.Parse("77777777-0000-7000-8000-00000000a001");
    internal static readonly Guid Caregiver = Guid.Parse("77777777-0000-7000-8000-00000000a002");
    internal static readonly Guid TrackerOwner = Guid.Parse("77777777-0000-7000-8000-00000000a003");
    internal static readonly Guid FormerMember = Guid.Parse("77777777-0000-7000-8000-00000000a004");
    internal static readonly Guid DeniedMember = Guid.Parse("77777777-0000-7000-8000-00000000a005");

    private static readonly Guid OwnerRole = Guid.Parse("77777777-0000-7000-8000-00000000c001");
    private static readonly Guid DeniedRole = Guid.Parse("77777777-0000-7000-8000-00000000c002");
    private static readonly Guid DeniedMembership = Guid.Parse("77777777-0000-7000-8000-00000000b002");
    private static readonly Guid OwnerMembership = Guid.Parse("77777777-0000-7000-8000-00000000b001");

    private static readonly Guid TrackerDefinition = Guid.Parse("77777777-0000-7000-8000-00000000d001");
    private static readonly Guid OrphanedTrackerDefinition = Guid.Parse("77777777-0000-7000-8000-00000000d002");
    private static readonly Guid DeniedTrackerDefinition = Guid.Parse("77777777-0000-7000-8000-00000000d003");

    internal static readonly Guid EditorInApp = Rule(1);
    internal static readonly Guid WebPushOnly = Rule(2);
    internal static readonly Guid InAppAndWebPush = Rule(3);
    internal static readonly Guid TwoWebPush = Rule(4);
    internal static readonly Guid TrackerSynced = Rule(5);
    internal static readonly Guid TrackerOfFormerMember = Rule(6);
    internal static readonly Guid TrackerOfDeniedMember = Rule(8);
    internal static readonly Guid AlreadyRouted = Rule(7);
    internal static readonly Guid OwnerlessInApp = Guid.Parse("88888888-0000-7000-8000-000000000e01");

    internal static readonly Guid WebPushOnlyChannel = Channel(2, 1);

    private static Guid Rule(int n) => Guid.Parse($"77777777-0000-7000-8000-000000000e0{n}");

    private static Guid Channel(int rule, int n) => Guid.Parse($"77777777-0000-7000-8000-0000000f0{rule}0{n}");

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("route_alert_channels");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(targetMigration: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    internal async Task<List<(string ChannelType, string Destination)>> ChannelsAsync(Guid tenantId, Guid ruleId)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT set_config('app.current_tenant_id', '{tenantId}', false);
            SELECT channel_type, destination FROM alert_rule_channels
             WHERE alert_rule_id = '{ruleId}' ORDER BY sort_order, id;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        var channels = new List<(string, string)>();
        while (await reader.ReadAsync())
            channels.Add((reader.GetString(0), reader.GetString(1)));
        return channels;
    }

    internal async Task<string?> ChannelTypeAsync(Guid tenantId, Guid channelId)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT set_config('app.current_tenant_id', '{tenantId}', false);
            SELECT channel_type FROM alert_rule_channels WHERE id = '{channelId}';
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        return await reader.ReadAsync() ? reader.GetString(0) : null;
    }

    private async Task ExecuteAsync(Guid tenantId, string sql)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT set_config('app.current_tenant_id', '{tenantId}', false); {sql}";
        await cmd.ExecuteNonQueryAsync();
    }

    private static string RuleRow(Guid tenantId, Guid ruleId, string? managedBy = null) => $"""
        INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                                 severity, client_configuration, is_enabled, allow_through_dnd, scope_class,
                                 sort_order, managed_by, created_at, updated_at)
        VALUES ('{ruleId}', '{tenantId}', 'Rule', 'threshold', '{"{}"}', false,
                'warning', '{"{}"}', true, false, 'undirected', 0,
                {(managedBy is null ? "NULL" : $"'{managedBy}'")}, now(), now());
        """;

    private static string ChannelRow(Guid tenantId, Guid ruleId, Guid channelId, string type, string destination, int sortOrder) => $"""
        INSERT INTO alert_rule_channels (id, tenant_id, alert_rule_id, channel_type, destination, sort_order, created_at)
        VALUES ('{channelId}', '{tenantId}', '{ruleId}', '{type}', '{destination}', {sortOrder}, now());
        """;

    private static string TrackerRow(Guid tenantId, Guid definitionId, string userId) => $"""
        INSERT INTO tracker_definitions (id, tenant_id, user_id, name, icon, category, trigger_event_types,
                                         is_favorite, dashboard_visibility, mode, visibility,
                                         low_reservoir_urgency, created_at)
        VALUES ('{definitionId}', '{tenantId}', '{userId}', 'Sensor', 'activity', 0, '[]',
                false, 0, 0, 0, 1, now());
        """;

    private async Task SeedAsync()
    {
        // The caregiver joins first, so "earliest owner" cannot be satisfied by the earliest member.
        await ExecuteAsync(Guid.Empty, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{Routes}',    'routes',    'Routes',    true, now(), now()),
              ('{Ownerless}', 'ownerless', 'Ownerless', true, now(), now());

            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status) VALUES
              ('{Owner}',        'Owner',        true, false, now(), now(), 'Approved'),
              ('{Caregiver}',    'Caregiver',    true, false, now(), now(), 'Approved'),
              ('{TrackerOwner}', 'TrackerOwner', true, false, now(), now(), 'Approved'),
              ('{FormerMember}', 'FormerMember', true, false, now(), now(), 'Approved'),
              ('{DeniedMember}', 'DeniedMember', true, false, now(), now(), 'Approved');

            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours) VALUES
              (gen_random_uuid(),   '{Routes}',    '{Caregiver}',    '[]'::jsonb, now() - interval '2 days', now(), false),
              ('{OwnerMembership}', '{Routes}',    '{Owner}',        '[]'::jsonb, now() - interval '1 day',  now(), false),
              (gen_random_uuid(),   '{Routes}',    '{TrackerOwner}', '["glucose.read"]'::jsonb, now(),      now(), false),
              ('{DeniedMembership}', '{Routes}',   '{DeniedMember}', '[]'::jsonb, now(),                     now(), false),
              (gen_random_uuid(),   '{Ownerless}', '{Owner}',        '[]'::jsonb, now(),                     now(), false);

            INSERT INTO tenant_roles (id, tenant_id, name, slug, permissions, is_system, sys_created_at, sys_updated_at)
            VALUES ('{OwnerRole}',  '{Routes}', 'Owner',  'owner',  '["*"]'::jsonb, true, now(), now()),
                   ('{DeniedRole}', '{Routes}', 'Denied', 'denied', '[]'::jsonb,    true, now(), now());
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            VALUES (gen_random_uuid(), '{OwnerMembership}',  '{OwnerRole}'),
                   (gen_random_uuid(), '{DeniedMembership}', '{DeniedRole}');
            """);

        await ExecuteAsync(Routes, $"""
            {TrackerRow(Routes, TrackerDefinition, TrackerOwner.ToString())}
            {TrackerRow(Routes, OrphanedTrackerDefinition, FormerMember.ToString())}
            {TrackerRow(Routes, DeniedTrackerDefinition, DeniedMember.ToString())}

            {RuleRow(Routes, EditorInApp)}
            {ChannelRow(Routes, EditorInApp, Channel(1, 1), "in_app", "", 0)}

            {RuleRow(Routes, WebPushOnly)}
            {ChannelRow(Routes, WebPushOnly, WebPushOnlyChannel, "web_push", "", 0)}

            {RuleRow(Routes, InAppAndWebPush)}
            {ChannelRow(Routes, InAppAndWebPush, Channel(3, 1), "web_push", "", 0)}
            {ChannelRow(Routes, InAppAndWebPush, Channel(3, 2), "in_app", Caregiver.ToString(), 1)}

            {RuleRow(Routes, TwoWebPush)}
            {ChannelRow(Routes, TwoWebPush, Channel(4, 1), "web_push", "", 0)}
            {ChannelRow(Routes, TwoWebPush, Channel(4, 2), "web_push", "default", 1)}

            {RuleRow(Routes, TrackerSynced, $"tracker:{TrackerDefinition}")}
            {ChannelRow(Routes, TrackerSynced, Channel(5, 1), "in_app", "", 0)}
            {ChannelRow(Routes, TrackerSynced, Channel(5, 2), "web_push", "", 1)}

            {RuleRow(Routes, TrackerOfFormerMember, $"tracker:{OrphanedTrackerDefinition}")}
            {ChannelRow(Routes, TrackerOfFormerMember, Channel(6, 1), "in_app", "", 0)}

            {RuleRow(Routes, TrackerOfDeniedMember, $"tracker:{DeniedTrackerDefinition}")}
            {ChannelRow(Routes, TrackerOfDeniedMember, Channel(8, 1), "in_app", "", 0)}

            {RuleRow(Routes, AlreadyRouted)}
            {ChannelRow(Routes, AlreadyRouted, Channel(7, 1), "in_app", Caregiver.ToString(), 0)}
            {ChannelRow(Routes, AlreadyRouted, Channel(7, 2), "webhook", "https://example.invalid/hook", 1)}
            """);

        await ExecuteAsync(Ownerless, $"""
            {RuleRow(Ownerless, OwnerlessInApp)}
            {ChannelRow(Ownerless, OwnerlessInApp, Guid.Parse("88888888-0000-7000-8000-0000000f0101"), "in_app", "", 0)}
            """);
    }
}

/// <inheritdoc cref="RouteAlertChannelsToMembersFixture"/>
[Trait("Category", "Integration")]
public class RouteAlertChannelsToMembersTests(RouteAlertChannelsToMembersFixture fixture)
    : IClassFixture<RouteAlertChannelsToMembersFixture>
{
    private static readonly Guid Routes = RouteAlertChannelsToMembersFixture.Routes;
    private static readonly string Owner = RouteAlertChannelsToMembersFixture.Owner.ToString();
    private static readonly string Caregiver = RouteAlertChannelsToMembersFixture.Caregiver.ToString();

    private Task<List<(string ChannelType, string Destination)>> ChannelsAsync(Guid ruleId) =>
        fixture.ChannelsAsync(Routes, ruleId);

    [Fact]
    public async Task A_blank_in_app_channel_goes_to_the_tenants_owner()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.EditorInApp))
            .Should().Equal(("in_app", Owner));
    }

    [Fact]
    public async Task A_rule_whose_only_channel_was_web_push_now_reaches_the_owner_in_app()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.WebPushOnly))
            .Should().Equal(("in_app", Owner));
        (await fixture.ChannelTypeAsync(Routes, RouteAlertChannelsToMembersFixture.WebPushOnlyChannel))
            .Should().Be("in_app", "the channel is converted in place, keeping its id");
    }

    [Fact]
    public async Task A_web_push_channel_beside_an_in_app_one_is_dropped()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.InAppAndWebPush))
            .Should().Equal(("in_app", Caregiver));
    }

    [Fact]
    public async Task Several_web_push_channels_become_one_in_app_channel()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.TwoWebPush))
            .Should().Equal(("in_app", Owner));
    }

    [Fact]
    public async Task A_tracker_synced_channel_goes_to_the_tracker_owner()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.TrackerSynced))
            .Should().Equal(("in_app", RouteAlertChannelsToMembersFixture.TrackerOwner.ToString()));
    }

    [Fact]
    public async Task A_tracker_owned_by_someone_no_longer_a_member_falls_back_to_the_owner()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.TrackerOfFormerMember))
            .Should().Equal(("in_app", Owner));
    }

    [Fact]
    public async Task A_tracker_owned_by_a_denied_member_falls_back_to_the_owner()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.TrackerOfDeniedMember))
            .Should().Equal(("in_app", Owner));
    }

    [Fact]
    public async Task A_routed_channel_and_other_channel_types_are_left_alone()
    {
        (await ChannelsAsync(RouteAlertChannelsToMembersFixture.AlreadyRouted))
            .Should().Equal(("in_app", Caregiver), ("webhook", "https://example.invalid/hook"));
    }

    [Fact]
    public async Task A_tenant_with_no_owner_keeps_a_blank_and_never_borrows_another_tenants_owner()
    {
        (await fixture.ChannelsAsync(RouteAlertChannelsToMembersFixture.Ownerless, RouteAlertChannelsToMembersFixture.OwnerlessInApp))
            .Should().Equal(("in_app", ""));
    }
}
