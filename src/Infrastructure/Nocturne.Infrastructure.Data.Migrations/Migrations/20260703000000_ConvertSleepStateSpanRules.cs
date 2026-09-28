using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertSleepStateSpanRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PR #226 removed the Sleep StateSpanCategory. Stored alert rules that referenced
            // it as a `state_span_active` leaf (condition_params category = "Sleep") now fail to
            // deserialize and throw every evaluation cycle. This migration converts them to the
            // dedicated `sleep_session_active` condition, which reads the sleep_sessions tables.
            //
            // Two shapes carry a Sleep reference:
            //   1. Top-level leaf   — condition_type = 'state_span_active',
            //                         condition_params = {"category":"Sleep","is_active":<bool>}.
            //                         Converted to condition_type = 'sleep_session_active',
            //                         condition_params = {"is_active":<bool>}.
            //   2. Nested envelope  — a node {"type":"state_span_active",
            //                         "state_span_active":{"category":"Sleep","is_active":<bool>}}
            //                         inside a composite/not/sustained tree. Converted to
            //                         {"type":"sleep_session_active",
            //                         "sleep_session_active":{"is_active":<bool>}}.
            //
            // is_active is preserved in both shapes. The tree walk is fully structural
            // (composite/not/sustained all recurse), so every case is convertible — no rule
            // needs to be disabled.
            //
            // alert_rules has FORCE ROW LEVEL SECURITY and the migrator role is NOBYPASSRLS, so
            // the table itself cannot be used to discover tenants — without app.current_tenant_id
            // set, RLS filters every row. Tenants are enumerated from the tenants table (not
            // tenant-scoped, no RLS policy) and the GUC is set per iteration so each UPDATE runs
            // inside that tenant's RLS context.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    -- Recursive rewriter for nested envelope nodes. Walks composite/not/sustained
                    -- wrappers and rewrites any Sleep state_span_active leaf in place. Defined for
                    -- the duration of this migration; dropped at the end.
                    CREATE OR REPLACE FUNCTION pg_temp.convert_sleep_node(node jsonb)
                    RETURNS jsonb AS $fn$
                    DECLARE
                        result jsonb := node;
                        children jsonb;
                        rewritten jsonb;
                        child jsonb;
                    BEGIN
                        IF node IS NULL OR jsonb_typeof(node) <> 'object' THEN
                            RETURN node;
                        END IF;

                        -- Leaf: state_span_active referencing the removed Sleep category.
                        IF node->>'type' = 'state_span_active'
                           AND node->'state_span_active'->>'category' = 'Sleep' THEN
                            RETURN jsonb_build_object(
                                'type', 'sleep_session_active',
                                'sleep_session_active', jsonb_build_object(
                                    'is_active',
                                    COALESCE(node->'state_span_active'->'is_active', 'true'::jsonb)
                                )
                            );
                        END IF;

                        -- composite: rewrite every child in the conditions array.
                        IF node->>'type' = 'composite' AND node->'composite' IS NOT NULL THEN
                            children := node->'composite'->'conditions';
                            IF children IS NOT NULL AND jsonb_typeof(children) = 'array' THEN
                                rewritten := '[]'::jsonb;
                                FOR child IN SELECT * FROM jsonb_array_elements(children)
                                LOOP
                                    rewritten := rewritten || jsonb_build_array(pg_temp.convert_sleep_node(child));
                                END LOOP;
                                result := jsonb_set(result, '{composite,conditions}', rewritten);
                            END IF;
                            RETURN result;
                        END IF;

                        -- not: rewrite the single child.
                        IF node->>'type' = 'not' AND node->'not'->'child' IS NOT NULL THEN
                            result := jsonb_set(result, '{not,child}',
                                pg_temp.convert_sleep_node(node->'not'->'child'));
                            RETURN result;
                        END IF;

                        -- sustained: rewrite the single child.
                        IF node->>'type' = 'sustained' AND node->'sustained'->'child' IS NOT NULL THEN
                            result := jsonb_set(result, '{sustained,child}',
                                pg_temp.convert_sleep_node(node->'sustained'->'child'));
                            RETURN result;
                        END IF;

                        RETURN node;
                    END;
                    $fn$ LANGUAGE plpgsql;

                    FOR r IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);

                        -- Shape 1: top-level Sleep state_span_active leaf.
                        UPDATE alert_rules
                        SET condition_type = 'sleep_session_active',
                            condition_params = jsonb_build_object(
                                'is_active',
                                COALESCE(condition_params->'is_active', 'true'::jsonb))
                        WHERE tenant_id = r.id
                          AND condition_type = 'state_span_active'
                          AND condition_params->>'category' = 'Sleep';

                        -- Shape 2: nested Sleep leaves inside composite/not/sustained trees.
                        -- Rewrite the whole tree structurally. The predicate limits the write to
                        -- trees whose serialized params contain a Sleep category; convert_sleep_node
                        -- only rewrites actual state_span_active Sleep leaves, so an over-broad text
                        -- match here is harmless (non-matching nodes are returned unchanged).
                        UPDATE alert_rules
                        SET condition_params = pg_temp.convert_sleep_node(condition_params)
                        WHERE tenant_id = r.id
                          AND condition_type IN ('composite', 'not', 'sustained')
                          AND condition_params::text LIKE '%"category": "Sleep"%';
                    END LOOP;

                    DROP FUNCTION IF EXISTS pg_temp.convert_sleep_node(jsonb);
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way data migration — the original StateSpanCategory value ("Sleep") no longer
            // exists in the model, so a faithful reversal is not possible.
        }
    }
}
