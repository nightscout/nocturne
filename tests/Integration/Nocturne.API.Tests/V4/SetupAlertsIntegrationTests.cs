using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Microsoft.EntityFrameworkCore;
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

    private static readonly object[] Webhook =
        [new { channelType = "webhook", destination = "https://example.invalid/alerts" }];

    private async Task<JsonElement> SendTestAsync() =>
        await ReadAsync(await AuthenticatedClient.PostAsync($"{Setup}/test", null));

    private async Task<HttpResponseMessage> ConfirmAsync(string instanceId, bool acknowledged = false) =>
        await AuthenticatedClient.PostAsJsonAsync(
            $"{Setup}/test/{instanceId}/received", new { acknowledgedOpenPageOnly = acknowledged });

    /// <summary>What the bot's callback records once an off-page channel has sent the alert.</summary>
    private Task MarkDeliveredAsync(string instanceId) =>
        SqlAsync("UPDATE alert_deliveries SET status = 'delivered' WHERE alert_instance_id = @i", ("i", Guid.Parse(instanceId)));

    /// <summary>Points every channel of the named starter rules somewhere else, as the rule builder can.</summary>
    private async Task RerouteAsync(ChannelType type, string destination, bool enable, params string[] ruleNames)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var rules = await db.AlertRules.Include(r => r.Channels).Where(r => ruleNames.Contains(r.Name)).ToListAsync();
        foreach (var rule in rules)
        {
            if (enable)
                rule.IsEnabled = true;
            foreach (var channel in rule.Channels)
            {
                channel.ChannelType = type;
                channel.Destination = destination;
            }
        }
        await db.SaveChangesAsync();
    }

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
            rule.GetProperty("allowThroughDnd").GetBoolean().Should().Be(
                rule.GetProperty("name").GetString() == "Urgent low", "urgent low always sounds through Do Not Disturb");
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

        var clearedWhileOff = new object[]
        {
            new { kind = "UrgentLow", isEnabled = false, threshold = (decimal?)null },
            new { kind = "Low", isEnabled = true, threshold = (decimal?)70 },
            new { kind = "High", isEnabled = true, threshold = (decimal?)250 },
            new { kind = "NoReadings", isEnabled = true, threshold = (decimal?)null },
        };
        await SaveAsync(clearedWhileOff);
        StoredValue((await AlertRulesByNameAsync())["Urgent low"]).Should().Be(55, "a switched-off rule keeps its threshold");
    }

    [Fact]
    public async Task Save_RefusesNoDestination_AndBrowserPush_AndAddressesABlankInAppChannelToTheCaller()
    {
        await SaveAsync(Rules(55, 70, 250), Array.Empty<object>(), HttpStatusCode.BadRequest);
        await SaveAsync(Rules(55, 70, 250), new object[] { new { channelType = "web_push" } }, HttpStatusCode.BadRequest);

        var saved = await SaveAsync(Rules(55, 70, 250), new object[] { new { channelType = "in_app" } });

        saved.GetProperty("toThisDevice").GetBoolean().Should().BeTrue();
        (await AlertRulesByNameAsync())["Low"].GetProperty("channels")[0].GetProperty("destination").GetString()
            .Should().Be(Fixture.OwnerSubjectId.ToString());
    }

    [Fact]
    public async Task TwoSavesAtOnce_MakeOneSetOfStarterRules()
    {
        await Task.WhenAll(
            AuthenticatedClient.PutAsJsonAsync(Setup, new { rules = Rules(55, 70, 250) }),
            AuthenticatedClient.PutAsJsonAsync(Setup, new { rules = Rules(55, 70, 250) }));

        (await AlertRulesByNameAsync()).Should().HaveCount(4);
    }

    [Fact]
    public async Task Caregiver_IsDoneOnlyByATestToADestinationThatWorksWithNocturneClosed()
    {
        await RelationshipAsync("Caregiver");
        var before = await GetSetupAsync();
        before.GetProperty("toThisDevice").GetBoolean().Should().BeFalse("a caregiver is steered off this device");
        before.GetProperty("needsDeliveryWhileClosed").GetBoolean().Should().BeTrue();
        before.GetProperty("channelTypesOffered").EnumerateArray().Select(t => t.GetString())
            .Should().Contain("telegram_dm").And.NotContain("web_push").And.NotContain("in_app");

        var onDevice = await SaveAsync(Rules(55, 70, 250));
        onDevice.GetProperty("routing").GetString().Should().Be("ToYouAsCaregiver");
        onDevice.GetProperty("deliversWhileClosed").GetBoolean().Should().BeFalse();
        foreach (var rule in (await AlertRulesByNameAsync()).Values)
            rule.GetProperty("allowThroughDnd").GetBoolean().Should().BeTrue();

        var deviceTest = (await SendTestAsync()).GetProperty("instanceId").GetString()!;
        (await ConfirmAsync(deviceTest, acknowledged: true)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await HubStateAsync()).Should().Be("Open");

        var offPage = await SaveAsync(Rules(55, 70, 250), Webhook);
        offPage.GetProperty("deliversWhileClosed").GetBoolean().Should().BeTrue();
        var webhookTest = (await SendTestAsync()).GetProperty("instanceId").GetString()!;
        await MarkDeliveredAsync(webhookTest);

        (await ReadAsync(await ConfirmAsync(webhookTest))).GetProperty("verified").GetBoolean().Should().BeTrue();
        (await HubStateAsync()).Should().Be("Done");
    }

    [Fact]
    public async Task Caregiver_IsNotDoneByADeviceChannel()
    {
        await RelationshipAsync("Caregiver");
        await SaveAsync(
            Rules(55, 70, 250),
            new object[] { new { channelType = "device_action", destination = "companion" } },
            HttpStatusCode.BadRequest);

        await SaveAsync(Rules(55, 70, 250));
        await RerouteAsync(ChannelType.DeviceAction, "companion", enable: false, "Urgent low", "Low", "High", "No readings for 20 minutes");
        (await GetSetupAsync()).GetProperty("deliversWhileClosed").GetBoolean().Should().BeFalse();

        var test = (await SendTestAsync()).GetProperty("instanceId").GetString()!;
        await MarkDeliveredAsync(test);

        (await ConfirmAsync(test, acknowledged: true)).StatusCode.Should().Be(
            HttpStatusCode.Conflict, "a device running its own engine takes nothing from a delivery");
        (await HubStateAsync()).Should().Be("Open");
    }

    [Fact]
    public async Task AConfirmedTest_StopsCounting_OnceAnotherStarterRuleSendsElsewhere()
    {
        await SaveAsync(Rules(55, 70, 250, urgentLowOn: false));
        var test = (await SendTestAsync()).GetProperty("instanceId").GetString()!;
        (await ReadAsync(await ConfirmAsync(test, acknowledged: true))).GetProperty("verified").GetBoolean().Should().BeTrue();

        await RerouteAsync(ChannelType.WebPush, "", enable: true, "Urgent low");

        (await GetSetupAsync()).GetProperty("verified").GetBoolean()
            .Should().BeFalse("urgent low was switched on sending somewhere the test never went");
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

        (await ConfirmAsync(Guid.CreateVersion7().ToString(), acknowledged: true))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ConfirmAsync(instanceId!)).StatusCode.Should().Be(
            HttpStatusCode.Conflict, "this device only shows alerts while Nocturne is open, which must be acknowledged");

        var confirmed = await ReadAsync(await ConfirmAsync(instanceId!, acknowledged: true));
        confirmed.GetProperty("verified").GetBoolean().Should().BeTrue();
        (await HubStateAsync()).Should().Be("Done");
    }

    [Fact]
    public async Task AConfirmedTest_StopsCounting_OnceAlertsGoSomewhereElse()
    {
        await SaveAsync(Rules(55, 70, 250));
        var test = (await SendTestAsync()).GetProperty("instanceId").GetString()!;
        (await ReadAsync(await ConfirmAsync(test, acknowledged: true))).GetProperty("verified").GetBoolean().Should().BeTrue();

        var moved = await SaveAsync(Rules(55, 70, 250), Webhook);

        moved.GetProperty("verified").GetBoolean().Should().BeFalse("the test never went to the webhook");
        (await ConfirmAsync(test, acknowledged: true)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ATestNocturneCouldNotSend_CannotBeConfirmed()
    {
        await SaveAsync(Rules(55, 70, 250), Webhook);
        var test = await SendTestAsync();
        test.GetProperty("deliveries")[0].GetProperty("status").GetString().Should().Be("failed");

        (await ConfirmAsync(test.GetProperty("instanceId").GetString()!, acknowledged: true))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
