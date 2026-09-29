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
/// The setup hub's Alerts item through the real API: starter rules land as ordinary alert rules
/// in mg/dL whatever the owner reads, route by who Nocturne is for, and the item is done only once
/// a test alert is confirmed received.
/// </summary>
[Trait("Category", "Integration")]
public class SetupAlertsIntegrationTests : ApiIntegrationTestBase
{
    private const string Setup = "/api/v4/setup-hub/alerts";

    public SetupAlertsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SqlAsync("""
            DELETE FROM setup_hub_items;
            UPDATE tenants SET patient_relationship = NULL, default_glucose_units = NULL;
            UPDATE subjects SET preferences = NULL;
            """);
    }

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task RelationshipAsync(string relationship) =>
        SqlAsync("UPDATE tenants SET patient_relationship = @r", ("r", relationship));

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private async Task<JsonElement> GetSetupAsync() => await ReadAsync(await AuthenticatedClient.GetAsync(Setup));

    private async Task<JsonElement> SaveAsync(object rules, object? channels = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        await ReadAsync(await AuthenticatedClient.PutAsJsonAsync(Setup, new { rules, channels }), expected);

    private static object[] Rules(decimal urgentLow, decimal low, decimal high, bool urgentLowOn = true) =>
    [
        new { kind = "UrgentLow", isEnabled = urgentLowOn, threshold = urgentLow },
        new { kind = "Low", isEnabled = true, threshold = low },
        new { kind = "High", isEnabled = true, threshold = high },
        new { kind = "NoReadings", isEnabled = true },
    ];

    private async Task<Dictionary<string, JsonElement>> AlertRulesByNameAsync()
    {
        var rules = await ReadAsync(await AuthenticatedClient.GetAsync("/api/v4/alert-rules"));
        return rules.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
    }

    private static decimal StoredValue(JsonElement rule) =>
        rule.GetProperty("conditionParams").GetProperty("value").GetDecimal();

    private async Task<string> HubStateAsync()
    {
        var hub = await ReadAsync(await AuthenticatedClient.GetAsync("/api/v4/setup-hub"));
        return hub.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == "Alerts")
            .GetProperty("state").GetString()!;
    }

    [Fact]
    public async Task StarterRules_AreCreatedAsAlertRulesInMgdl_SentToTheOwnerOnThisDevice()
    {
        var before = await GetSetupAsync();
        before.GetProperty("saved").GetBoolean().Should().BeFalse();
        before.GetProperty("routing").GetString().Should().Be("ToYou");
        before.GetProperty("glucoseUnits").GetString().Should().Be("mg/dl");
        before.GetProperty("rules").EnumerateArray()
            .Select(r => r.GetProperty("threshold").ValueKind == JsonValueKind.Null ? (decimal?)null : r.GetProperty("threshold").GetDecimal())
            .Should().Equal(55m, 70m, 250m, null);

        var saved = await SaveAsync(Rules(55, 70, 250));
        saved.GetProperty("saved").GetBoolean().Should().BeTrue();
        saved.GetProperty("toThisDevice").GetBoolean().Should().BeTrue();

        var rules = await AlertRulesByNameAsync();
        rules.Keys.Should().BeEquivalentTo("Urgent low", "Low", "High", "No readings for 20 minutes");
        StoredValue(rules["Urgent low"]).Should().Be(55);
        rules["Urgent low"].GetProperty("severity").GetString().Should().Be("critical");
        StoredValue(rules["High"]).Should().Be(250);
        rules["No readings for 20 minutes"].GetProperty("conditionType").GetString().Should().Be("signal_loss");
        rules["No readings for 20 minutes"].GetProperty("conditionParams").GetProperty("timeout_minutes").GetInt32().Should().Be(20);
        foreach (var rule in rules.Values)
        {
            var channel = rule.GetProperty("channels").EnumerateArray().Should().ContainSingle().Subject;
            channel.GetProperty("channelType").GetString().Should().Be("in_app");
            channel.GetProperty("destination").GetString().Should().Be(Fixture.OwnerSubjectId.ToString());
        }

        await SaveAsync(Rules(55, 70, 250));
        (await AlertRulesByNameAsync()).Should().HaveCount(4, "saving again updates the starter rules rather than adding more");
    }

    [Fact]
    public async Task Thresholds_AreShownAndReadInMmol_AndStoredInMgdl()
    {
        await SqlAsync("UPDATE tenants SET default_glucose_units = 'mmol'");

        var before = await GetSetupAsync();
        before.GetProperty("glucoseUnits").GetString().Should().Be("mmol");
        before.GetProperty("rules").EnumerateArray().Take(3)
            .Select(r => r.GetProperty("threshold").GetDecimal())
            .Should().Equal(3.1m, 3.9m, 13.9m);

        await SaveAsync(Rules(3.1m, 3.9m, 14.0m));

        var rules = await AlertRulesByNameAsync();
        StoredValue(rules["Urgent low"]).Should().Be(55, "a value read back unchanged keeps the stored mg/dL");
        StoredValue(rules["Low"]).Should().Be(70);
        StoredValue(rules["High"]).Should().Be(252);
        (await GetSetupAsync()).GetProperty("rules")[2].GetProperty("threshold").GetDecimal().Should().Be(14.0m);
    }

    [Fact]
    public async Task EveryRuleIsToggleable_UrgentLowIncluded_AndThresholdsAreBounded()
    {
        await SaveAsync(Rules(55, 70, 250, urgentLowOn: false));
        var off = await AlertRulesByNameAsync();
        off["Urgent low"].GetProperty("isEnabled").GetBoolean().Should().BeFalse();
        off["Low"].GetProperty("isEnabled").GetBoolean().Should().BeTrue();
        (await GetSetupAsync()).GetProperty("rules")[0].GetProperty("isEnabled").GetBoolean().Should().BeFalse();

        await SaveAsync(Rules(55, 70, 250));
        (await AlertRulesByNameAsync())["Urgent low"].GetProperty("isEnabled").GetBoolean().Should().BeTrue();

        await SaveAsync(Rules(5, 70, 250), expected: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Caregiver_HasAlertsSentToThemselves_OvernightIncluded()
    {
        await RelationshipAsync("Caregiver");

        var saved = await SaveAsync(Rules(55, 70, 250));

        saved.GetProperty("routing").GetString().Should().Be("ToYouAsCaregiver");
        saved.GetProperty("toThisDevice").GetBoolean().Should().BeTrue();
        foreach (var rule in (await AlertRulesByNameAsync()).Values)
            rule.GetProperty("allowThroughDnd").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Helper_LeavesAlertsToThePersonTheyHandOverTo()
    {
        await RelationshipAsync("Helper");

        (await GetSetupAsync()).GetProperty("routing").GetString().Should().Be("LeftForRecipient");
        await SaveAsync(Rules(55, 70, 250), expected: HttpStatusCode.Conflict);
        (await AuthenticatedClient.PostAsync($"{Setup}/test", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AlertRulesByNameAsync()).Should().BeEmpty();
        (await HubStateAsync()).Should().Be("Open", "the item is left for the recipient");
    }

    [Fact]
    public async Task Self_CanAlertAMemberToUrgentLows_WhoStaysThereThroughASave_AndIsNotSentTheTest()
    {
        await RelationshipAsync("Self");
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Partner");

            var saved = await SaveAsync(Rules(55, 70, 250));
            saved.GetProperty("members").EnumerateArray().Should().ContainSingle()
                .Which.GetProperty("alertedToUrgentLows").GetBoolean().Should().BeFalse();

            var added = await ReadAsync(await AuthenticatedClient.PutAsJsonAsync(
                $"{Setup}/urgent-low-recipients/{memberId}", new { alerted = true }));
            added.GetProperty("members")[0].GetProperty("alertedToUrgentLows").GetBoolean().Should().BeTrue();

            await SaveAsync(Rules(55, 72, 250));

            var rules = await AlertRulesByNameAsync();
            rules["Urgent low"].GetProperty("channels").EnumerateArray()
                .Select(c => c.GetProperty("destination").GetString())
                .Should().BeEquivalentTo(Fixture.OwnerSubjectId.ToString(), memberId.ToString());
            rules["Low"].GetProperty("channels").EnumerateArray().Should().ContainSingle();
            (await GetSetupAsync()).GetProperty("toThisDevice").GetBoolean().Should().BeTrue();

            var test = await ReadAsync(await AuthenticatedClient.PostAsync($"{Setup}/test", null));
            test.GetProperty("deliveries").EnumerateArray().Should().ContainSingle("the test goes only where the owner's alerts go");
        }
    }

    [Fact]
    public async Task TheItemIsDone_OnlyOnceATestAlertIsConfirmedReceived()
    {
        await SaveAsync(Rules(55, 70, 250));
        (await HubStateAsync()).Should().Be("Open", "a rule alone does not say its alerts reach anyone");

        var test = await ReadAsync(await AuthenticatedClient.PostAsync($"{Setup}/test", null));
        var instanceId = test.GetProperty("instanceId").GetString();
        test.GetProperty("ruleName").GetString().Should().Be("Urgent low");
        test.GetProperty("deliveries").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("channelType").GetString().Should().Be("in_app");
        (await HubStateAsync()).Should().Be("Open", "sending a test is not knowing it arrived");

        (await AuthenticatedClient.PostAsync($"{Setup}/test/{Guid.CreateVersion7()}/received", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var confirmed = await ReadAsync(await AuthenticatedClient.PostAsync($"{Setup}/test/{instanceId}/received", null));
        confirmed.GetProperty("verified").GetBoolean().Should().BeTrue();
        (await HubStateAsync()).Should().Be("Done");
    }
}
