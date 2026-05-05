using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// Integration tests for the V4 BasalInjection controller. Exercises the full
/// controller -> repository -> DB stack with the <c>MutationAuditInterceptor</c>
/// wired so soft-delete audit entries can be observed end-to-end. Also covers
/// idempotency on (DataSource, SyncIdentifier), the soft-delete-then-recreate
/// flow that the partial unique index permits, and Row Level Security tenant
/// isolation.
/// </summary>
[Trait("Category", "Integration")]
public class BasalInjectionIntegrationTests : AspireIntegrationTestBase
{
    private const string EntityType = "BasalInjection";

    private Guid _tenantAId;
    private Guid _subjectAId;
    private string _accessTokenA = null!;
    private string _slugA = null!;
    private string _baseDomain = null!;
    private string _connectionString = null!;

    public BasalInjectionIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _connectionString = (await GetPostgresConnectionStringAsync())!;

        // Touch the API once to trigger first-run tenant provisioning.
        using var bootstrapClient = CreateAuthenticatedClient();
        await bootstrapClient.GetAsync("/api/v1/status");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        _tenantAId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectAId, _accessTokenA) = await AuthTestHelpers
            .SeedAuthenticatedSubjectAsync(conn, _tenantAId, $"BasalInj A {Guid.NewGuid():N}");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM tenants WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _tenantAId);
            _slugA = (string)(await cmd.ExecuteScalarAsync())!;
        }

        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
    }

    [Fact]
    public async Task POST_creates_row_and_writes_audit_entry()
    {
        var insulinId = await SeedBasalPatientInsulinAsync(_tenantAId);
        using var client = CreateClient(_slugA, _accessTokenA);

        var (status, body) = await PostBasalInjectionAsync(client, insulinId, units: 12.0);

        status.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();
        id.Should().NotBe(Guid.Empty);

        // Row exists in DB (RLS-scoped).
        (await CountBasalInjectionsAsync(_tenantAId, id, includeDeleted: true))
            .Should().Be(1, "the controller should have inserted exactly one row");

        // Create-action audit entry written by the interceptor.
        var auditRows = await GetAuditEntriesAsync(_tenantAId, id);
        auditRows.Should().ContainSingle(a => a.Action == "create",
            "create operation must produce a single 'create' audit entry");
        auditRows.Single(a => a.Action == "create").SubjectId.Should().Be(_subjectAId);
    }

    [Fact]
    public async Task DELETE_soft_deletes_row_and_writes_audit_entry_via_interceptor()
    {
        var insulinId = await SeedBasalPatientInsulinAsync(_tenantAId);
        using var client = CreateClient(_slugA, _accessTokenA);

        var (status, body) = await PostBasalInjectionAsync(client, insulinId, units: 8.0);
        status.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();

        var deleteResponse = await client.DeleteAsync($"/api/v4/insulin/basal-injections/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Soft-deleted row remains in DB but with deleted_at set.
        (await CountBasalInjectionsAsync(_tenantAId, id, includeDeleted: false))
            .Should().Be(0, "soft-deleted row must be hidden from default queries");
        (await CountBasalInjectionsAsync(_tenantAId, id, includeDeleted: true))
            .Should().Be(1, "soft-deleted row must remain physically present");

        // Interceptor should have written a 'delete' audit entry on the
        // null -> non-null DeletedAt transition.
        var auditRows = await GetAuditEntriesAsync(_tenantAId, id);
        auditRows.Should().Contain(a => a.Action == "delete",
            "MutationAuditInterceptor must record a 'delete' entry on soft-delete");
        var deleteEntry = auditRows.Single(a => a.Action == "delete");
        deleteEntry.SubjectId.Should().Be(_subjectAId);
        deleteEntry.EntityType.Should().Be(EntityType);
        deleteEntry.EntityId.Should().Be(id);
    }

    [Fact]
    public async Task GET_after_DELETE_returns_404()
    {
        var insulinId = await SeedBasalPatientInsulinAsync(_tenantAId);
        using var client = CreateClient(_slugA, _accessTokenA);

        var (status, body) = await PostBasalInjectionAsync(client, insulinId, units: 10.0);
        status.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();

        var deleteResponse = await client.DeleteAsync($"/api/v4/insulin/basal-injections/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync($"/api/v4/insulin/basal-injections/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "soft-deleted records are filtered out of reads by the global query filter");
    }

    [Fact]
    public async Task POST_with_same_DataSource_SyncIdentifier_is_idempotent()
    {
        var insulinId = await SeedBasalPatientInsulinAsync(_tenantAId);
        using var client = CreateClient(_slugA, _accessTokenA);

        var dataSource = "test-source";
        var syncId = $"sync-{Guid.NewGuid():N}";

        var (status1, body1) = await PostBasalInjectionAsync(
            client, insulinId, units: 14.0, dataSource: dataSource, syncIdentifier: syncId);
        status1.Should().Be(HttpStatusCode.Created);
        var id1 = body1.GetProperty("id").GetGuid();

        // Re-post with the same (DataSource, SyncIdentifier).
        var (status2, body2) = await PostBasalInjectionAsync(
            client, insulinId, units: 14.0, dataSource: dataSource, syncIdentifier: syncId);

        status2.Should().Be(HttpStatusCode.OK,
            "duplicate (DataSource, SyncIdentifier) submissions short-circuit to the existing row");
        var id2 = body2.GetProperty("id").GetGuid();
        id2.Should().Be(id1, "idempotent upsert must return the same record id");

        // Only one row exists.
        var rowCount = await CountBySyncIdentifierAsync(_tenantAId, dataSource, syncId, includeDeleted: false);
        rowCount.Should().Be(1, "the partial unique index forbids a second live row for the same sync key");
    }

    [Fact]
    public async Task POST_with_same_SyncIdentifier_after_soft_delete_creates_new_row()
    {
        var insulinId = await SeedBasalPatientInsulinAsync(_tenantAId);
        using var client = CreateClient(_slugA, _accessTokenA);

        var dataSource = "test-source";
        var syncId = $"sync-{Guid.NewGuid():N}";

        // Create and soft-delete the first row.
        var (status1, body1) = await PostBasalInjectionAsync(
            client, insulinId, units: 12.0, dataSource: dataSource, syncIdentifier: syncId);
        status1.Should().Be(HttpStatusCode.Created);
        var id1 = body1.GetProperty("id").GetGuid();

        var deleteResponse = await client.DeleteAsync($"/api/v4/insulin/basal-injections/{id1}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Re-post with the same sync identifier; the partial unique index
        // (... WHERE sync_identifier IS NOT NULL AND deleted_at IS NULL) must
        // permit a second live row.
        var (status2, body2) = await PostBasalInjectionAsync(
            client, insulinId, units: 13.5, dataSource: dataSource, syncIdentifier: syncId);
        status2.Should().Be(HttpStatusCode.Created,
            "after soft-delete the sync key is free again and a new row should be inserted");

        var id2 = body2.GetProperty("id").GetGuid();
        id2.Should().NotBe(id1, "the new insertion must be a distinct row, not the resurrected one");

        // One live row, two physical rows.
        (await CountBySyncIdentifierAsync(_tenantAId, dataSource, syncId, includeDeleted: false))
            .Should().Be(1, "exactly one live row per sync key");
        (await CountBySyncIdentifierAsync(_tenantAId, dataSource, syncId, includeDeleted: true))
            .Should().Be(2, "the soft-deleted row remains in storage");
    }

    [Fact]
    public async Task Tenant_A_cannot_read_Tenant_B_rows()
    {
        // Seed an isolated tenant B with its own subject and insulin.
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var tenantBSlug = $"tenant-b-{Guid.NewGuid():N}".Substring(0, 24);
        var tenantBId = await AuthTestHelpers.SeedTenantAsync(conn, tenantBSlug, "Basal Tenant B");
        var (_, accessTokenB) = await AuthTestHelpers
            .SeedAuthenticatedSubjectAsync(conn, tenantBId, $"BasalInj B {Guid.NewGuid():N}");

        var insulinB = await SeedBasalPatientInsulinAsync(tenantBId);

        // Tenant B writes a basal injection.
        using var clientB = CreateClient(tenantBSlug, accessTokenB);
        var (statusB, bodyB) = await PostBasalInjectionAsync(clientB, insulinB, units: 11.0);
        statusB.Should().Be(HttpStatusCode.Created);
        var idB = bodyB.GetProperty("id").GetGuid();

        // Tenant A must NOT see the row via either GET-by-id or list.
        using var clientA = CreateClient(_slugA, _accessTokenA);

        var byIdResponse = await clientA.GetAsync($"/api/v4/insulin/basal-injections/{idB}");
        byIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "RLS must prevent tenant A from observing tenant B's record by id");

        var listResponse = await clientA.GetAsync("/api/v4/insulin/basal-injections");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listContent = await listResponse.Content.ReadAsStringAsync();
        listContent.Should().NotContain(idB.ToString(),
            "RLS must hide tenant B's record from tenant A's list query");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private HttpClient CreateClient(string slug, string accessToken) =>
        AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, slug, _baseDomain, accessToken);

    private async Task<Guid> SeedBasalPatientInsulinAsync(Guid tenantId)
    {
        var insulinId = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await SetTenantContextAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO patient_insulins (
                id, tenant_id, insulin_category, name, start_date, end_date,
                is_current, dia, peak, curve, concentration, role, is_primary,
                sys_created_at, sys_updated_at
            ) VALUES (
                @id, @tenantId, 'LongActing', 'Lantus', @start, @end,
                true, 24.0, 600, 'long-acting', 100, 'Basal', true,
                now(), now()
            );
            """;
        cmd.Parameters.AddWithValue("id", insulinId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        // Active window safely spans "now": started a year ago, no end date.
        cmd.Parameters.AddWithValue("start", DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)));
        cmd.Parameters.AddWithValue("end", DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        return insulinId;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostBasalInjectionAsync(
        HttpClient client,
        Guid patientInsulinId,
        double units,
        string? dataSource = null,
        string? syncIdentifier = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-1),
            ["utcOffset"] = 0,
            ["device"] = "integration-test-device",
            ["app"] = "integration-test",
            ["patientInsulinId"] = patientInsulinId,
            ["units"] = units,
            ["notes"] = "integration test"
        };
        if (dataSource is not null)
            payload["dataSource"] = dataSource;
        if (syncIdentifier is not null)
            payload["syncIdentifier"] = syncIdentifier;

        var response = await client.PostAsJsonAsync("/api/v4/insulin/basal-injections", payload);
        var content = await response.Content.ReadAsStringAsync();

        JsonElement body = default;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                body = JsonSerializer.Deserialize<JsonElement>(content);
            }
            catch (JsonException)
            {
                body = default;
            }
        }
        return (response.StatusCode, body);
    }

    private async Task<int> CountBasalInjectionsAsync(Guid tenantId, Guid id, bool includeDeleted)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await SetTenantContextAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeDeleted
            ? "SELECT COUNT(*) FROM basal_injections WHERE id = @id;"
            : "SELECT COUNT(*) FROM basal_injections WHERE id = @id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("id", id);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private async Task<int> CountBySyncIdentifierAsync(
        Guid tenantId, string dataSource, string syncIdentifier, bool includeDeleted)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await SetTenantContextAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeDeleted
            ? "SELECT COUNT(*) FROM basal_injections WHERE data_source = @ds AND sync_identifier = @sid;"
            : "SELECT COUNT(*) FROM basal_injections WHERE data_source = @ds AND sync_identifier = @sid AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("ds", dataSource);
        cmd.Parameters.AddWithValue("sid", syncIdentifier);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private async Task<List<AuditRow>> GetAuditEntriesAsync(Guid tenantId, Guid entityId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await SetTenantContextAsync(conn, tenantId);

        var rows = new List<AuditRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entity_type, entity_id, action, subject_id
            FROM mutation_audit_log
            WHERE entity_type = @entityType AND entity_id = @entityId
            ORDER BY created_at;
            """;
        cmd.Parameters.AddWithValue("entityType", EntityType);
        cmd.Parameters.AddWithValue("entityId", entityId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                Id: reader.GetGuid(0),
                EntityType: reader.GetString(1),
                EntityId: reader.GetGuid(2),
                Action: reader.GetString(3),
                SubjectId: reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }
        return rows;
    }

    private static async Task SetTenantContextAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
        cmd.Parameters.AddWithValue("tenantId", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private record AuditRow(Guid Id, string EntityType, Guid EntityId, string Action, Guid? SubjectId);
}
