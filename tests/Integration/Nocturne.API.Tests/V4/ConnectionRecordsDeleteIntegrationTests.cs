using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// <c>POST /api/v4/services/connections/delete-records</c> on PostgreSQL: a connection's records go
/// by the credential that wrote them, so another writer's records under the same device name stay.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ConnectionRecordsDeleteIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/services/connections/delete-records";
    private const string SharedDevice = "Unknown";

    /// <summary>A direct grant of the tenant owner, who the api-secret client signs in as.</summary>
    private async Task<Guid> SeedOwnTokenAsync()
    {
        var grantId = Guid.CreateVersion7();
        await using var conn = await OpenPostgresAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await conn.PinTenantAsync(Fixture.TenantId, local: true);
        await conn.ExecuteAsync("""
            INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, created_at)
            VALUES (@id, @tenant, @subject, 'direct', ARRAY['*'], 'uploader', now());
            """, ("tenant", Fixture.TenantId), ("id", grantId), ("subject", Fixture.OwnerSubjectId));
        await transaction.CommitAsync();
        return grantId;
    }

    /// <summary>
    /// An install of the verified app <paramref name="softwareId"/> by the tenant owner: its own client
    /// registration and an app grant, revoked when <paramref name="revoked"/>.
    /// </summary>
    private async Task<Guid> SeedOwnAppInstallAsync(string softwareId, bool revoked)
    {
        var clientId = Guid.CreateVersion7();
        var grantId = Guid.CreateVersion7();
        await using var conn = await OpenPostgresAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await conn.PinTenantAsync(Fixture.TenantId, local: true);
        await conn.ExecuteAsync("""
            INSERT INTO oauth_clients (id, tenant_id, client_id, software_id, display_name, is_known, redirect_uris)
            VALUES (@client, @tenant, @clientKey, @software, 'Companion', true, '[]');
            INSERT INTO oauth_grants (id, tenant_id, client_id, subject_id, grant_type, scopes, created_at, revoked_at)
            VALUES (@id, @tenant, @client, @subject, 'app', ARRAY['*'], now() - interval '90 days',
                    CASE WHEN @revoked THEN now() - interval '45 days' END);
            """,
            ("tenant", Fixture.TenantId), ("client", clientId), ("clientKey", $"companion-{clientId:N}"),
            ("software", softwareId), ("id", grantId), ("subject", Fixture.OwnerSubjectId), ("revoked", revoked));
        await transaction.CommitAsync();
        return grantId;
    }

    /// <summary>The direct grant of a new member of the tenant.</summary>
    private async Task<Guid> SeedOtherMembersTokenAsync()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Other member");
        await using var transaction = await conn.BeginTransactionAsync();
        await conn.PinTenantAsync(Fixture.TenantId, local: true);
        var grants = await conn.QueryAsync("""
            SELECT id FROM oauth_grants WHERE subject_id = @subject;
            """,
            reader => reader.GetGuid(0), ("subject", subjectId));
        await transaction.CommitAsync();
        grants.Should().NotBeEmpty();
        return grants[0];
    }

    /// <summary>A sensor reading, a meter reading and a bolus from <paramref name="writer"/>, under <see cref="SharedDevice"/>.</summary>
    private async Task SeedWrittenAsync(Guid writer, int minutesAgo)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        db.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, Device = SharedDevice, Timestamp = at, Mgdl = 120,
            WriterGrantId = writer,
        });
        db.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, Device = SharedDevice, Timestamp = at, Mgdl = 110,
            WriterGrantId = writer,
        });
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, LegacyId = $"bolus-{writer:N}", Device = SharedDevice,
            Timestamp = at, Insulin = 1, WriterGrantId = writer,
        });
        await db.SaveChangesAsync();
    }

    private async Task<HashSet<Guid?>> LiveWritersAsync()
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return
        [
            .. await db.SensorGlucose.Select(e => e.WriterGrantId).ToListAsync(),
            .. await db.MeterGlucose.Select(e => e.WriterGrantId).ToListAsync(),
            .. await db.Boluses.Select(e => e.WriterGrantId).ToListAsync(),
        ];
    }

    private Task<HttpResponseMessage> DeleteAsync(bool allowReimport, params Guid[] grantIds) =>
        AuthenticatedClient.PostAsJsonAsync(Path, new { grantIds, allowReimport });

    [Fact]
    public async Task Deleting_a_connections_records_leaves_another_writers_under_the_same_device()
    {
        var own = await SeedOwnTokenAsync();
        var other = await SeedOtherMembersTokenAsync();
        await SeedWrittenAsync(own, 5);
        await SeedWrittenAsync(other, 6);

        using var response = await DeleteAsync(false, own);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var counts = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deletedCounts");
        counts.GetProperty("Glucose").GetInt64().Should().Be(1);
        counts.GetProperty("ManualBG").GetInt64().Should().Be(1);
        counts.GetProperty("Boluses").GetInt64().Should().Be(1);
        (await LiveWritersAsync()).Should().BeEquivalentTo(new Guid?[] { other });

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var deleted = await db.SensorGlucose.IgnoreQueryFilters().SingleAsync(e => e.WriterGrantId == own);
        db.Entry(deleted).Property<bool>("DeletedByUser").CurrentValue.Should().BeTrue(
            "a user's delete holds the reading against a re-upload");
    }

    [Fact]
    public async Task Deleting_an_app_covers_its_revoked_installs_and_records_older_than_the_list_shows()
    {
        var shown = await SeedOwnAppInstallAsync("org.example.companion", revoked: false);
        var revoked = await SeedOwnAppInstallAsync("org.example.companion", revoked: true);
        var other = await SeedOtherMembersTokenAsync();
        await SeedWrittenAsync(shown, 5);
        await SeedWrittenAsync(revoked, 60 * 24 * 60);
        await SeedWrittenAsync(other, 6);

        using var response = await DeleteAsync(false, shown);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await LiveWritersAsync()).Should().BeEquivalentTo(new Guid?[] { other });
    }

    [Fact]
    public async Task A_grant_of_another_member_is_refused_and_nothing_is_deleted()
    {
        var own = await SeedOwnTokenAsync();
        var other = await SeedOtherMembersTokenAsync();
        await SeedWrittenAsync(own, 5);
        await SeedWrittenAsync(other, 6);

        using var alone = await DeleteAsync(false, other);
        using var mixed = await DeleteAsync(false, own, other);

        alone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mixed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LiveWritersAsync()).Should().BeEquivalentTo(new Guid?[] { own, other });
    }

    [Fact]
    public async Task Allowing_reimport_releases_only_the_connections_tombstones()
    {
        var own = await SeedOwnTokenAsync();
        var otherOwn = await SeedOwnTokenAsync();
        await SeedWrittenAsync(own, 5);
        await SeedWrittenAsync(otherOwn, 6);
        (await DeleteAsync(false, otherOwn)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await DeleteAsync(true, own);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var readings = await db.SensorGlucose.IgnoreQueryFilters().ToDictionaryAsync(e => e.WriterGrantId!.Value);
        readings.Values.Should().OnlyContain(e => e.DeletedAt != null);
        db.Entry(readings[own]).Property<bool>("DeletedByUser").CurrentValue.Should().BeFalse();
        db.Entry(readings[otherOwn]).Property<bool>("DeletedByUser").CurrentValue.Should().BeTrue(
            "another connection's tombstone under the same device stays");
    }
}
