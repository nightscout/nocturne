using FluentAssertions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Regression coverage for the <c>ux_translation_drafts_logical_key</c> unique index.
///
/// The index must be scoped by <c>tenant_id</c>. Subjects are global membership scopes,
/// so one person can hold drafts in two tenants; a global index made the second tenant's
/// insert collide with a row that tenant's RLS policy cannot see. The service's
/// unique-violation retry re-reads through the same policy, finds nothing, re-inserts and
/// rethrows — a 500 on an autosave keystroke that wedges that message for that user.
///
/// Raw NpgsqlConnection (not EF) so the assertion is about what PostgreSQL actually
/// enforces after the full migration chain runs, independent of the ORM.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class TranslationDraftUniqueIndexTests(RlsCompletenessFixture fx)
{
    [Fact]
    public async Task OneSubject_CanDraftTheSameMessage_InTwoTenants()
    {
        var subject = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.SeedTenantAsync(tenantA, $"draft-{tenantA:N}", "draft-index-test");
        await conn.SeedTenantAsync(tenantB, $"draft-{tenantB:N}", "draft-index-test");

        await conn.PinTenantAsync(tenantA);
        await InsertDraftAsync(conn, tenantA, subject, "fr", "", "Hello");

        await conn.PinTenantAsync(tenantB);
        var act = () => InsertDraftAsync(conn, tenantB, subject, "fr", "", "Hello");

        await act.Should().NotThrowAsync(
            "the draft logical key is scoped per tenant; a subject who belongs to two tenants " +
            "can legitimately draft the same message in both");
    }

    [Fact]
    public async Task SameTenantAndSubject_CannotInsert_DuplicateLogicalKey()
    {
        var subject = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.SeedTenantAsync(tenant, $"draft-{tenant:N}", "draft-index-test");
        await conn.PinTenantAsync(tenant);

        await InsertDraftAsync(conn, tenant, subject, "fr", "", "Hello");
        var act = () => InsertDraftAsync(conn, tenant, subject, "fr", "", "Hello");

        var thrown = await act.Should().ThrowAsync<PostgresException>();
        thrown.Which.SqlState.Should().Be(
            "23505",
            "within one tenant a subject holds at most one draft per (locale, context, msgid)");
    }

    private static async Task InsertDraftAsync(
        NpgsqlConnection conn, Guid tenantId, Guid subjectId, string locale, string context, string msgId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO translation_drafts
                (id, tenant_id, subject_id, locale, msgctxt, msgid, translations, created_at, updated_at)
            VALUES
                (gen_random_uuid(), @tid, @sid, @locale, @ctx, @msgid, '["x"]'::jsonb, now(), now())
            """;
        AddParam(cmd, "@tid", tenantId);
        AddParam(cmd, "@sid", subjectId);
        AddParam(cmd, "@locale", locale);
        AddParam(cmd, "@ctx", context);
        AddParam(cmd, "@msgid", msgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParam(NpgsqlCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
