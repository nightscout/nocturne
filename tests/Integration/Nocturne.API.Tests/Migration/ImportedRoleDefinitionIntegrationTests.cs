using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// A Nightscout import reads the source's role definitions to work out each imported token's scopes.
/// It adds a role the instance lacks to the global roles every tenant shares only on an instance with
/// one tenant, and never under the reserved platform-admin name. The imported token's scopes come
/// from the source's definitions either way.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class ImportedRoleDefinitionIntegrationTests
    : ApiIntegrationTestBase, IClassFixture<ImportedRoleDefinitionIntegrationTests.NightscoutAuthorizationSource>
{
    private const string CustomRole = "import-standing-logger";
    private const string ReservedSpelling = "Platform_Admin";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] ReaderScopes =
    [
        Scope.GlucoseRead,
        Scope.TreatmentsRead,
        Scope.TreatmentsReadWrite,
        Scope.DevicesRead,
        Scope.TherapyRead,
        Scope.FoodRead,
        Scope.AlertsRead,
        Scope.ReportsRead,
        Scope.IdentityRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead,
    ];

    private readonly NightscoutAuthorizationSource _source;

    public ImportedRoleDefinitionIntegrationTests(
        ApiIntegrationTestFixture fixture, NightscoutAuthorizationSource source, ITestOutputHelper output)
        : base(fixture, output)
    {
        _source = source;
    }

    public enum Instance
    {
        OneTenant,
        TwoTenants,
    }

    [Theory]
    [InlineData(Instance.OneTenant)]
    [InlineData(Instance.TwoTenants)]
    public async Task An_import_adds_a_global_role_only_on_an_instance_with_one_tenant(Instance instance)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantA = await AuthTestHelpers.GetTenantIdAsync(conn);
        Guid? tenantB = null;
        try
        {
            if (instance == Instance.TwoTenants)
                tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, $"import-b-{Guid.NewGuid():N}"[..20], "Import B");

            await ImportAsOwnerAsync(conn, tenantA);

            (await AuthTestHelpers.RoleCountAsync(conn, CustomRole)).Should().Be(instance == Instance.OneTenant ? 1 : 0);
            (await GrantScopesAsync(conn, tenantA, "Reader")).Should().BeEquivalentTo(ReaderScopes);
        }
        finally
        {
            await RemoveImportedRolesAsync(conn);
            if (tenantB is { } id)
                await conn.ExecuteAsync("DELETE FROM tenants WHERE id = @id;", ("id", id));
        }
    }

    [Theory]
    [InlineData(Instance.OneTenant)]
    [InlineData(Instance.TwoTenants)]
    public async Task An_import_adds_no_role_under_the_reserved_platform_admin_name(Instance instance)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantA = await AuthTestHelpers.GetTenantIdAsync(conn);
        Guid? tenantB = null;
        try
        {
            if (instance == Instance.TwoTenants)
                tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, $"import-b-{Guid.NewGuid():N}"[..20], "Import B");

            await ImportAsOwnerAsync(conn, tenantA);

            (await ReservedRoleCountAsync(conn)).Should().Be(0);
            (await GrantScopesAsync(conn, tenantA, "Operator")).Should().BeEquivalentTo([Scope.GlucoseRead]);
        }
        finally
        {
            await RemoveImportedRolesAsync(conn);
            if (tenantB is { } id)
                await conn.ExecuteAsync("DELETE FROM tenants WHERE id = @id;", ("id", id));
        }
    }

    /// <summary>
    /// Starts a subjects import from <see cref="NightscoutAuthorizationSource"/> as an owner of the
    /// tenant who is not a platform admin, and runs it to its end.
    /// </summary>
    private async Task ImportAsOwnerAsync(NpgsqlConnection conn, Guid tenantId)
    {
        var slug = await SlugAsync(conn, tenantId);
        using var owner = AuthTestHelpers.CreateTenantBearerClient(
            Fixture, slug, AuthTestHelpers.GetBaseDomain(ApiClient),
            await AuthTestHelpers.IssueSessionTokenAsync(Fixture, await OwnerAsync(conn, tenantId)));

        var started = await owner.PostAsJsonAsync("/api/v4/migration/start", new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = _source.Url,
            Collections = ["subjects"],
        });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var job = await started.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);

        await MigrationJobPump.RunAsync(Fixture);

        var status = await owner.GetFromJsonAsync<MigrationJobStatus>($"/api/v4/migration/{job!.Id}/status", JsonOptions);
        status!.State.Should().Be(MigrationJobState.Completed, status.ErrorMessage);
    }

    private static async Task<Guid> OwnerAsync(NpgsqlConnection conn, Guid tenantId)
    {
        var (ownerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Import Owner");
        await conn.ExecuteAsync("""
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            SELECT gen_random_uuid(), id, @roleId FROM tenant_members WHERE tenant_id = @tenantId AND subject_id = @subjectId;
            """,
            ("roleId", await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, "owner")),
            ("tenantId", tenantId), ("subjectId", ownerId));
        return ownerId;
    }

    private static async Task<string[]> GrantScopesAsync(NpgsqlConnection conn, Guid tenantId, string label)
    {
        await conn.PinTenantAsync(tenantId);
        return await conn.ScalarAsync<string[]>("SELECT scopes FROM oauth_grants WHERE tenant_id = @tenant AND label = @label;", ("tenant", tenantId), ("label", label));
    }

    private static async Task RemoveImportedRolesAsync(NpgsqlConnection conn) =>
        await conn.ExecuteAsync("DELETE FROM roles WHERE name = @custom OR lower(name) = @reserved;",
            ("custom", CustomRole), ("reserved", PlatformAdminCredentials.RoleName));

    private static async Task<long> ReservedRoleCountAsync(NpgsqlConnection conn)
    {
        return await conn.ScalarAsync<long>("SELECT count(*) FROM roles WHERE lower(name) = @reserved;", ("reserved", PlatformAdminCredentials.RoleName));
    }

    private static async Task<string> SlugAsync(NpgsqlConnection conn, Guid tenantId)
    {
        return await conn.ScalarAsync<string>("SELECT slug FROM tenants WHERE id = @id;", ("id", tenantId));
    }

    /// <summary>
    /// A source Nightscout instance serving its role definitions and two subjects: one holding the
    /// seeded <c>readable</c> role and a custom role, one holding a role spelt as the reserved
    /// platform-admin name.
    /// </summary>
    public sealed class NightscoutAuthorizationSource : IAsyncLifetime
    {
        private WebApplication? _app;

        public string Url { get; private set; } = "";

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            _app = builder.Build();

            _app.MapGet("/api/v1/status", () => Results.Json(new { status = "ok", name = "Nightscout", version = "15.0.0" }));
            _app.MapGet("/api/v2/authorization/roles", () => Results.Json(new object[]
            {
                new { name = "readable", permissions = new[] { "*:*:read" } },
                new { name = CustomRole, permissions = new[] { "api:treatments:*" } },
                new { name = ReservedSpelling, permissions = new[] { "api:entries:read" } },
            }));
            _app.MapGet("/api/v2/authorization/subjects", () => Results.Json(new object[]
            {
                new { _id = "5f1a00000000000000000101", name = "Reader", roles = new[] { "readable", CustomRole }, accessToken = "reader-0123456789abcdef" },
                new { _id = "5f1a00000000000000000102", name = "Operator", roles = new[] { ReservedSpelling }, accessToken = "operator-0123456789abcdef" },
            }));

            await _app.StartAsync();
            Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        }

        public async Task DisposeAsync()
        {
            if (_app is not null)
                await _app.DisposeAsync();
        }
    }
}
