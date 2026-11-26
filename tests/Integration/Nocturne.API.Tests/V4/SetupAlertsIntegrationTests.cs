using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// The setup hub's Alerts item through the real API: starter rules land as ordinary alert rules
/// in mg/dL whatever the owner reads, route by who Nocturne is for, and the item is done only once
/// a test alert is confirmed received.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
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
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync(sql, parameters);
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

    private static (bool Enabled, string? Direction, decimal? Value) ResolveOf(JsonElement rule)
    {
        var enabled = rule.GetProperty("autoResolveEnabled").GetBoolean();
        if (rule.GetProperty("autoResolveParams").ValueKind != JsonValueKind.Object)
            return (enabled, null, null);
        var threshold = rule.GetProperty("autoResolveParams").GetProperty("threshold");
        return (enabled, threshold.GetProperty("direction").GetString(), threshold.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task AGlucoseStarterRule_ClosesOnTheOtherSideOfItsThreshold_UntilTheOwnerChangesThat()
    {
        await SaveAsync(Rules(55, 70, 250));

        var rules = await AlertRulesByNameAsync();
        ResolveOf(rules["Urgent low"]).Should().Be((true, "above", 55m));
        ResolveOf(rules["Low"]).Should().Be((true, "above", 70m));
        ResolveOf(rules["High"]).Should().Be((true, "below", 250m));
        ResolveOf(rules["No readings for 20 minutes"]).Should().Be((false, null, null));

        await SaveAsync(Rules(55, 80, 250));
        ResolveOf((await AlertRulesByNameAsync())["Low"]).Should().Be((true, "above", 80m), "the resolve condition follows the threshold");

        await using (var db = Fixture.CreateDbContext(Fixture.TenantId))
        {
            var high = await db.AlertRules.SingleAsync(r => r.Name == "High");
            high.AutoResolveParams = """{"type":"threshold","threshold":{"direction":"below","value":180}}""";
            var urgentLow = await db.AlertRules.SingleAsync(r => r.Name == "Urgent low");
            urgentLow.AutoResolveEnabled = false;
            await db.SaveChangesAsync();
        }

        await SaveAsync(Rules(60, 80, 240));
        rules = await AlertRulesByNameAsync();
        StoredValue(rules["High"]).Should().Be(240);
        ResolveOf(rules["High"]).Should().Be((true, "below", 180m), "a resolve condition the owner edited stays");
        ResolveOf(rules["Urgent low"]).Should().Be((false, "above", 55m), "one the owner switched off stays off and unchanged");
    }

    /// <summary>
    /// Sets a starter rule's stored conditions as the rule editor would, and gives it a re-arm hold, so a
    /// save that changes its conditions shows by clearing the hold.
    /// </summary>
    private async Task<Guid> EditStarterAsync(
        string name, string conditionParams, AlertConditionType conditionType = AlertConditionType.Threshold)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var rule = await db.AlertRules.SingleAsync(r => r.Name == name);
        rule.ConditionType = conditionType;
        rule.ConditionParams = conditionParams;
        db.AlertTrackerState.Add(new AlertTrackerStateEntity
        {
            AlertRuleId = rule.Id, TenantId = Fixture.TenantId, State = "idle", AwaitingRearm = true,
        });
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private async Task<(string ConditionParams, bool AwaitingRearm)> StoredAsync(Guid ruleId)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var rule = await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == ruleId);
        var tracker = await db.AlertTrackerState.AsNoTracking().SingleAsync(t => t.AlertRuleId == ruleId);
        return (rule.ConditionParams, tracker.AwaitingRearm);
    }

    private async Task<string?> StoredResolveAsync(Guid ruleId)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return (await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == ruleId)).AutoResolveParams;
    }

    /// <summary>
    /// The Low starter after the rule editor stored <paramref name="conditionParams"/> for it: shown as set in the
    /// rule editor with no number, and a save that sends a number for it anyway leaves its conditions, its resolve
    /// condition and its re-arm hold exactly as stored.
    /// </summary>
    private async Task AssertSetInTheRuleEditorAndLeftAlone(
        string conditionParams, AlertConditionType conditionType = AlertConditionType.Threshold)
    {
        await SaveAsync(Rules(55, 70, 250));
        var low = await EditStarterAsync("Low", conditionParams, conditionType);
        var before = (await StoredAsync(low)).ConditionParams;
        var resolveBefore = await StoredResolveAsync(low);

        var shown = (await GetSetupAsync()).GetProperty("rules")[1];
        shown.GetProperty("threshold").ValueKind.Should().Be(JsonValueKind.Null);
        shown.GetProperty("setInRuleEditor").GetBoolean().Should().BeTrue();

        await SaveAsync(Rules(55, 90, 250), Webhook);

        (await StoredAsync(low)).Should().Be((before, true), "a level sent for a rule set in the rule editor is ignored");
        (await StoredResolveAsync(low)).Should().Be(resolveBefore);
    }

    [Fact]
    public async Task AStarterHeldForATime_IsShownAsSetInTheRuleEditor_AndALevelSentForItIsIgnored() =>
        await AssertSetInTheRuleEditorAndLeftAlone(
            """{"minutes":15,"child":{"type":"threshold","threshold":{"direction":"below","value":65}}}""",
            AlertConditionType.Sustained);

    [Fact]
    public async Task AStarterMadeIntoAnotherKindWithAValue_IsShownAsSetInTheRuleEditor() =>
        await AssertSetInTheRuleEditorAndLeftAlone(
            """{"operator":"<","value":70,"within_minutes":30}""", AlertConditionType.Predicted);

    [Fact]
    public async Task AStarterWhoseLimitNamesAFactThisBuildDoesNotKnow_IsShownAsSetInTheRuleEditor() =>
        await AssertSetInTheRuleEditorAndLeftAlone("""{"direction":"below","value_from":{"fact":"no_such_fact","offset":0}}""");

    [Theory]
    [InlineData("""{"direction":"below","value":"70"}""")]
    [InlineData("""{"direction":"below","value":null}""")]
    [InlineData("""[70]""")]
    public async Task AStarterWhoseValueIsNotANumber_IsShownAsSetInTheRuleEditor(string conditionParams) =>
        await AssertSetInTheRuleEditorAndLeftAlone(conditionParams);

    [Fact]
    public async Task ANoReadingsStarter_IsNeverShownAsSetInTheRuleEditor()
    {
        await SaveAsync(Rules(55, 70, 250));

        var shown = (await GetSetupAsync()).GetProperty("rules")[3];
        shown.GetProperty("setInRuleEditor").GetBoolean().Should().BeFalse();
        shown.GetProperty("threshold").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AStarterWhoseLevelIsATarget_IsShownAsSetInTheRuleEditor_AndASaveLeavesItAsItIs()
    {
        await SaveAsync(Rules(55, 70, 250));
        const string belowTarget = """{"direction":"below","value_from":{"fact":"target_low","offset":15}}""";
        var low = await EditStarterAsync("Low", belowTarget);
        var before = (await StoredAsync(low)).ConditionParams;

        var shown = (await GetSetupAsync()).GetProperty("rules")[1];
        shown.GetProperty("threshold").ValueKind.Should().Be(JsonValueKind.Null);
        shown.GetProperty("setInRuleEditor").GetBoolean().Should().BeTrue();

        await SaveAsync(
            new object[]
            {
                new { kind = "UrgentLow", isEnabled = true, threshold = 55 },
                new { kind = "Low", isEnabled = true },
                new { kind = "High", isEnabled = true, threshold = 250 },
                new { kind = "NoReadings", isEnabled = true },
            },
            Webhook);

        (await StoredAsync(low)).Should().Be((before, true), "a save that changes only a channel leaves the rule and its hold alone");
    }

    [Fact]
    public async Task AStarterWithAClearMargin_KeepsItsConditionsByteForByte_WhenItsNumberIsUnchanged()
    {
        await SaveAsync(Rules(55, 70, 250));
        const string withMargin = """{"direction":"below","value":70,"clear_margin":5}""";
        var low = await EditStarterAsync("Low", withMargin);
        var before = (await StoredAsync(low)).ConditionParams;

        await SaveAsync(Rules(55, 70, 250), Webhook);

        (await StoredAsync(low)).Should().Be((before, true));
    }

    [Fact]
    public async Task AStarterWithAClearMargin_KeepsTheMargin_WhenItsNumberChanges()
    {
        await SaveAsync(Rules(55, 70, 250));
        var low = await EditStarterAsync("Low", """{"direction":"below","value":70,"clear_margin":5}""");

        await SaveAsync(Rules(55, 75, 250));

        var (conditionParams, awaitingRearm) = await StoredAsync(low);
        var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(conditionParams)!;
        stored.Keys.Should().BeEquivalentTo("direction", "value", "clear_margin");
        stored["direction"].GetString().Should().Be("below");
        stored["value"].GetDecimal().Should().Be(75);
        stored["clear_margin"].GetDecimal().Should().Be(5);
        awaitingRearm.Should().BeFalse("a changed threshold re-arms the rule");
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
        await using (var conn = await OpenPostgresAsync())
        {
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
