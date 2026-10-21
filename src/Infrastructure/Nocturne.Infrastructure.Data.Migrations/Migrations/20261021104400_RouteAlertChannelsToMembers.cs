using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RouteAlertChannelsToMembers : Migration
    {
        /// <summary>
        /// Gives every stored alert channel that reached no one a member to reach. A
        /// <c>web_push</c> channel had no receiver, and an <c>in_app</c> channel saved by the rule
        /// editor or a tracker sync held no destination, yet both were recorded delivered.
        /// </summary>
        /// <remarks>
        /// A rule's first <c>web_push</c> channel becomes an <c>in_app</c> one when the rule has no
        /// <c>in_app</c> channel; every other <c>web_push</c> channel is dropped, as the rule already
        /// reaches the notification centre. A blank <c>in_app</c> destination then goes to the
        /// tracker definition's owner for a tracker-synced rule, when they are still an active
        /// member whose roles or direct permissions grant any access (the denied role grants
        /// none), and otherwise to the tenant's earliest owner. A tenant with no owner keeps the
        /// blank, which is recorded failed on delivery. Every table touched is tenant-scoped, hence
        /// the per-tenant GUC.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    owner_id uuid;
                BEGIN
                    FOR t IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE alert_rule_channels
                           SET channel_type = 'in_app', destination = ''
                         WHERE id IN (
                             SELECT id
                               FROM (
                                   SELECT w.id,
                                          row_number() OVER (
                                              PARTITION BY w.alert_rule_id
                                              ORDER BY w.sort_order, w.id) AS rn
                                     FROM alert_rule_channels w
                                    WHERE w.tenant_id = t.id
                                      AND w.channel_type = 'web_push'
                                      AND NOT EXISTS (
                                          SELECT 1
                                            FROM alert_rule_channels i
                                           WHERE i.alert_rule_id = w.alert_rule_id
                                             AND i.channel_type = 'in_app')
                               ) ranked
                              WHERE ranked.rn = 1);

                        DELETE FROM alert_rule_channels
                         WHERE tenant_id = t.id
                           AND channel_type = 'web_push';

                        owner_id := (
                            SELECT tm.subject_id
                              FROM tenant_members tm
                              JOIN tenant_member_roles tmr ON tmr.tenant_member_id = tm.id
                              JOIN tenant_roles tr ON tr.id = tmr.tenant_role_id
                              JOIN subjects s ON s.id = tm.subject_id
                             WHERE tm.tenant_id = t.id
                               AND tr.slug = 'owner'
                               AND s.is_active
                               AND NOT s.is_system_subject
                             ORDER BY tm.sys_created_at, tm.id
                             LIMIT 1);

                        UPDATE alert_rule_channels c
                           SET destination = COALESCE(
                               (SELECT tm.subject_id::text
                                  FROM alert_rules r
                                  JOIN tracker_definitions d ON r.managed_by = 'tracker:' || d.id::text
                                  JOIN tenant_members tm
                                    ON tm.tenant_id = t.id
                                   AND tm.subject_id::text = lower(d.user_id)
                                  JOIN subjects s ON s.id = tm.subject_id
                                 WHERE r.id = c.alert_rule_id
                                   AND s.is_active
                                   AND NOT s.is_system_subject
                                   AND (jsonb_array_length(COALESCE(tm.direct_permissions, '[]'::jsonb)) > 0
                                        OR EXISTS (
                                            SELECT 1
                                              FROM tenant_member_roles mr
                                              JOIN tenant_roles mrr ON mrr.id = mr.tenant_role_id
                                             WHERE mr.tenant_member_id = tm.id
                                               AND jsonb_array_length(mrr.permissions) > 0))
                                 LIMIT 1),
                               owner_id::text,
                               '')
                         WHERE c.tenant_id = t.id
                           AND c.channel_type = 'in_app'
                           AND btrim(c.destination) = '';
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
