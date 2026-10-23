using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>The tenant, callers and reads every Loop Analysis endpoint's integration tests share.</summary>
public abstract class LoopAnalysisIntegrationTestBase(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    /// <summary>2024-06-10 in Sydney (UTC+10, no DST in June) begins at this instant.</summary>
    protected static readonly DateTime WeekStart = new(2024, 6, 9, 14, 0, 0, DateTimeKind.Utc);
    protected static readonly DateTime PreviousWeekStart = WeekStart.AddDays(-7);

    /// <summary>The local week from <see cref="WeekStart"/>, as query parameters.</summary>
    protected const string Week = "startDate=2024-06-10&endDate=2024-06-16";

    /// <summary>A scope whose database contexts write as the fixture's tenant.</summary>
    protected IServiceScope TenantScope()
    {
        var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, "loop-analysis", "Loop analysis", true, false));
        return scope;
    }

    protected async Task SeedProfileAsync(string timezone)
    {
        static object[] Values(double value) => [new { time = "00:00", value }];
        var document = new
        {
            defaultProfile = "Default",
            startDate = "2024-01-01T00:00:00.000Z",
            mills = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            units = "mg/dl",
            store = new Dictionary<string, object>
            {
                ["Default"] = new
                {
                    dia = 4, timezone, units = "mg/dl",
                    basal = Values(1.0),
                    carbratio = Values(10),
                    sens = Values(50),
                    target_low = Values(90),
                    target_high = Values(140),
                },
            },
        };
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/profile", document);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    protected async Task<HttpClient> CreateHistoryClampedClientAsync()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (subjectId, token) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Clamped Follower");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                UPDATE oauth_grants SET limit_to_24_hours = true WHERE subject_id = @subject;
                """;
            cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
            cmd.Parameters.AddWithValue("subject", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }
        return AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, token);
    }

    protected async Task<HttpClient> CreateShareClientAsync()
    {
        using var owner = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId));
        var link = await (await owner.PostAsync("/api/v4/share/rotate", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        (await owner.PutAsJsonAsync("/api/v4/share/scopes", new { scopes = new[] { Scope.GlucoseRead, Scope.TreatmentsRead, Scope.ReportsRead } }))
            .EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v4/share/full-history", new { fullHistory = true }))
            .EnsureSuccessStatusCode();

        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Host = new Uri(link.GetProperty("url").GetString()!).Authority;
        return client;
    }

    protected async Task<HttpClient> CreateTokenClientAsync(params string[] scopes)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Loop analysis member");
        var accessToken = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);

        using var member = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
        var response = await member.PostAsJsonAsync("/api/auth/direct-grants", new { label = "loop-analysis", scopes });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        var token = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("token").GetString()!;
        return AuthTestHelpers.CreateBearerClient(Fixture, token);
    }

    protected static async Task<JsonElement> GetOkAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    protected static bool Absent(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;
}
