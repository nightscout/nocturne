using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nocturne.API.Models.Responses;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Hubs;

/// <summary>
/// For long polling SignalR serves a connection from a copy of the upgrade request whose DI scope
/// never ran the middleware, so the hub reads tenant data as no one unless the reader the request
/// recorded is set on it (<see cref="Nocturne.API.Hubs.HubConnectionReader"/>). <c>LoadRetro</c> reads
/// through that scope.
/// </summary>
[Trait("Category", "Integration")]
public class LongPollingReaderIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private async Task SeedEntriesAsync()
    {
        var entries = new[] { _now.AddHours(-30), _now.AddHours(-1) }
            .Select(at => new { type = "sgv", sgv = 120, date = at.ToUnixTimeMilliseconds(), dateString = at.ToString("O") })
            .ToArray();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", entries)).EnsureSuccessStatusCode();
    }

    private HubConnection LongPolling(
        string host, Action<IDictionary<string, string>> headers, string hub = ServiceNames.DataHub)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(ApiClient.BaseAddress!, $"hubs/{hub}"),
                HttpTransportType.LongPolling,
                options =>
                {
                    options.Headers["X-Forwarded-Host"] = host;
                    headers(options.Headers);
                })
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .Build();
        HubConnections.Add(connection);
        return connection;
    }

    private async Task<List<long>> RetroEntryMillsAsync(HubConnection connection)
    {
        var retro = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("retroUpdate", payload => retro.TrySetResult(payload));
        await connection.StartAsync();

        await connection.InvokeAsync("LoadRetro", new { loadedMills = _now.AddMinutes(1).ToUnixTimeMilliseconds() });

        var payload = await retro.Task.WaitAsync(TimeSpan.FromSeconds(20));
        payload.TryGetProperty("error", out _).Should().BeFalse(payload.GetRawText());
        return payload.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("mills").GetInt64())
            .ToList();
    }

    private async Task<string> SeedClampedMemberTokenAsync()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (_, token) = await AuthTestHelpers.SeedHistoryClampedSubjectAsync(
            conn, Fixture.TenantId, "Clamped Follower");
        return token;
    }

    [Fact]
    public async Task An_owner_over_long_polling_reads_the_tenants_history()
    {
        await SeedEntriesAsync();

        var mills = await RetroEntryMillsAsync(
            LongPolling(Fixture.TenantHost, headers => headers["api-secret"] = TestApiSecret));

        mills.Should().HaveCount(2, "the clamped case below is only meaningful if the older entry is served at all");
    }

    [Fact]
    public async Task A_clamped_member_over_long_polling_reads_only_the_last_24_hours()
    {
        await SeedEntriesAsync();
        var token = await SeedClampedMemberTokenAsync();

        var mills = await RetroEntryMillsAsync(
            LongPolling(Fixture.TenantHost, headers => headers["Authorization"] = $"Bearer {token}"));

        mills.Should().ContainSingle()
            .Which.Should().BeGreaterThan(_now.AddHours(-24).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_share_over_long_polling_reads_nothing_through_the_hub()
    {
        await SeedEntriesAsync();
        using var owner = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId));
        var link = await (await owner.PostAsync("/api/v4/share/rotate", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ShareLinkDto>();
        (await owner.PutAsJsonAsync("/api/v4/share/scopes", new { scopes = new[] { Scope.GlucoseRead } }))
            .EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v4/share/full-history", new { fullHistory = true }))
            .EnsureSuccessStatusCode();
        var connection = LongPolling(new Uri(link!.Url!).Authority, _ => { });
        var retro = false;
        connection.On<JsonElement>("retroUpdate", _ => retro = true);
        await connection.StartAsync();

        var load = () => connection.InvokeAsync(
            "LoadRetro", new { loadedMills = _now.AddMinutes(1).ToUnixTimeMilliseconds() });

        await load.Should().ThrowAsync<HubException>();
        retro.Should().BeFalse();
    }

    [Fact]
    public async Task A_write_over_long_polling_is_audited_as_the_connections_subject()
    {
        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, Fixture.TenantId);
        var (excursionId, _) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, Fixture.TenantId, ruleId);
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (subjectId, token) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Caregiver");
        var connection = LongPolling(
            Fixture.TenantHost, headers => headers["Authorization"] = $"Bearer {token}", hub: "alerts");
        await connection.StartAsync();

        await connection.InvokeAsync("Acknowledge", "caregiver");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant, false);
            SELECT subject_id FROM mutation_audit_log WHERE entity_id = @excursion;
            """;
        cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
        cmd.Parameters.AddWithValue("excursion", excursionId);
        var subjects = new List<Guid?>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
                subjects.Add(reader.IsDBNull(0) ? null : reader.GetGuid(0));
        }

        subjects.Should().NotBeEmpty().And.OnlyContain(s => s == subjectId,
            "the long-polling copy of the request never ran AuditContextMiddleware");
    }
}
