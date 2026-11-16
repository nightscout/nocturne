using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Legacy writes that name a treatment by the id Nocturne served for it: a v1 POST that re-sends a
/// read-back document, and edits and deletes of a meal whose bolus and carb halves do not share a
/// legacy id.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
[Parity]
public class LegacyTreatmentIdWriteIntegrationTests : ApiIntegrationTestBase
{
    public LegacyTreatmentIdWriteIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly DateTimeOffset TimeBase = DateTimeOffset.UtcNow.AddMinutes(-90);
    private static int _timeSlot;
    private static int _mealHour;

    private static DateTimeOffset DistinctTime() => DateTimeOffset.FromUnixTimeMilliseconds(
        TimeBase.AddMilliseconds(-1_001L * Interlocked.Increment(ref _timeSlot)).ToUnixTimeMilliseconds());

    private static string Iso(DateTimeOffset at) =>
        at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private Task<IReadOnlyList<Treatment>> TreatmentsAtAsync(DateTimeOffset at)
    {
        var mills = at.ToUnixTimeMilliseconds();
        return WithTenantScopeAsync(sp =>
            sp.GetRequiredService<ITreatmentService>().GetTreatmentsByRangeAsync(mills, mills));
    }

    private static async Task<string> PostForIdAsync(HttpClient client, object treatment)
    {
        var response = await client.PostAsJsonAsync("/api/v1/treatments", new[] { treatment });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString()!;
    }

    /// <summary>One upload of each treatment type the legacy API decomposes into a record table or a state span.</summary>
    public static TheoryData<string> TreatmentTypes => new()
    {
        "Meal Bolus", "Correction Bolus", "Carb Correction", "BG Check", "Note", "Site Change",
        "Temp Basal", "Bolus Wizard", "Temporary Target", "Temporary Override", "Profile Switch",
    };

    private static Dictionary<string, object> Upload(string eventType, DateTimeOffset at, string? syncIdentifier)
    {
        var body = new Dictionary<string, object>
        {
            ["eventType"] = eventType,
            ["created_at"] = Iso(at),
            ["enteredBy"] = "restore-script",
        };
        if (syncIdentifier is not null)
            body["syncIdentifier"] = syncIdentifier;

        switch (eventType)
        {
            case "Meal Bolus":
                body["insulin"] = 3.0;
                body["carbs"] = 30;
                break;
            case "Correction Bolus":
                body["insulin"] = 1.5;
                break;
            case "Carb Correction":
                body["carbs"] = 12;
                break;
            case "BG Check":
                body["glucose"] = 120;
                body["glucoseType"] = "Finger";
                body["units"] = "mg/dl";
                break;
            case "Note":
                body["notes"] = "first";
                break;
            case "Site Change":
                body["notes"] = "left arm";
                break;
            case "Temp Basal":
                body["absolute"] = 0.8;
                body["duration"] = 30;
                break;
            case "Bolus Wizard":
                body["insulin"] = 2.0;
                body["glucose"] = 160;
                body["units"] = "mg/dl";
                break;
            case "Temporary Target":
                body["reason"] = "Activity";
                body["duration"] = 45;
                body["targetTop"] = 140;
                body["targetBottom"] = 140;
                body["units"] = "mg/dl";
                break;
            case "Temporary Override":
                body["reason"] = "Exercise";
                body["duration"] = 60;
                break;
            case "Profile Switch":
                body["profile"] = "Default";
                body["duration"] = 60;
                break;
        }

        return body;
    }

    [Theory]
    [MemberData(nameof(TreatmentTypes))]
    public async Task Post_OfADocumentReadBack_ReplacesTheTreatmentItNames(string eventType)
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        var id = await PostForIdAsync(client, Upload(eventType, at, Guid.NewGuid().ToString().ToUpperInvariant()));

        var readBack = (await client.GetFromJsonAsync<JsonObject>($"/api/v1/treatments/{id}"))!;
        readBack["_id"]!.GetValue<string>().Should().Be(id);
        readBack["notes"] = "re-sent";

        var resentId = await PostForIdAsync(client, readBack);

        resentId.Should().Be(id);
        (await TreatmentsAtAsync(at)).Should().ContainSingle()
            .Which.Notes.Should().Be("re-sent");
    }

    [Theory]
    [MemberData(nameof(TreatmentTypes))]
    public async Task Post_ThenDeleteByTheReturnedId_LeavesNothing(string eventType)
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        var id = await PostForIdAsync(client, Upload(eventType, at, syncIdentifier: null));

        var delete = await client.DeleteAsync($"/api/v1/treatments/{id}");

        delete.IsSuccessStatusCode.Should().BeTrue(await delete.Content.ReadAsStringAsync());
        (await TreatmentsAtAsync(at)).Should().BeEmpty();
    }

    [Fact]
    public async Task Post_WithAFreshObjectIdShapedLikeAServedOne_KeepsItAsTheClientId()
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        var objectId = MongoObjectId.FromGuid(Guid.CreateVersion7());
        var body = Upload("Carb Correction", at, syncIdentifier: null);
        body["_id"] = objectId;

        await PostForIdAsync(client, body);
        await PostForIdAsync(client, body);

        var stored = await WithTenantScopeAsync(sp =>
            sp.GetRequiredService<ICarbIntakeRepository>().GetByLegacyIdAsync(objectId));
        stored.Should().NotBeNull();
        (await TreatmentsAtAsync(at)).Should().ContainSingle();
    }

    #region Meals whose halves do not share a legacy id

    public enum MealOrigin { NativeMeal, GlookoPair }

    public enum MealEdit { V1Put, V1PutCollection, V3Put, V3Patch }

    /// <summary>
    /// A meal entered in the Nocturne UI (both halves keyless) or imported by the Glooko connector
    /// (a distinct legacy and sync id per half), served under the bolus's id.
    /// </summary>
    private async Task<string> StoreMealAsync(MealOrigin origin, DateTimeOffset at)
    {
        if (origin == MealOrigin.NativeMeal)
        {
            using var client = CreateAuthenticatedClient();
            var response = await client.PostAsJsonAsync("/api/v4/nutrition/meals", new
            {
                timestamp = at,
                insulin = 4.0,
                carbs = 40.0,
            });
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }
        else
        {
            var correlationId = Guid.CreateVersion7();
            var suffix = at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            await WithTenantScopeAsync(async sp =>
            {
                await sp.GetRequiredService<IBolusRepository>().CreateAsync(new V4Models.Bolus
                {
                    Timestamp = at.UtcDateTime,
                    LegacyId = "glooko_bolus_" + suffix,
                    SyncIdentifier = "glooko_bolus_" + suffix,
                    Device = "glooko-connector",
                    DataSource = "glooko-connector",
                    Insulin = 4.0,
                    BolusType = V4Models.BolusType.Normal,
                    CorrelationId = correlationId,
                }, WriteOrigin.Live);
                await sp.GetRequiredService<ICarbIntakeRepository>().CreateAsync(new V4Models.CarbIntake
                {
                    Timestamp = at.UtcDateTime,
                    LegacyId = "glooko_carbs_" + suffix,
                    SyncIdentifier = "glooko_carbs_" + suffix,
                    Device = "glooko-connector",
                    DataSource = "glooko-connector",
                    Carbs = 40.0,
                    CorrelationId = correlationId,
                }, WriteOrigin.Live);
                return true;
            });
        }

        var meal = (await TreatmentsAtAsync(at)).Should().ContainSingle().Which;
        meal.EventType.Should().Be("Meal Bolus");
        meal.Carbs.Should().Be(40);
        return meal.Id!;
    }

    private static JsonObject Edited(string id, DateTimeOffset at) => new()
    {
        ["_id"] = id,
        ["eventType"] = "Meal Bolus",
        ["created_at"] = Iso(at),
        ["insulin"] = 5.0,
        ["carbs"] = 55,
    };

    private static async Task<HttpResponseMessage> EditAsync(HttpClient client, MealEdit edit, string id, DateTimeOffset at) =>
        edit switch
        {
            MealEdit.V1Put => await client.PutAsJsonAsync($"/api/v1/treatments/{id}", Edited(id, at)),
            MealEdit.V1PutCollection => await client.PutAsJsonAsync("/api/v1/treatments", Edited(id, at)),
            MealEdit.V3Put => await client.PutAsJsonAsync($"/api/v3/treatments/{id}", Edited(id, at)),
            MealEdit.V3Patch => await client.PatchAsJsonAsync(
                $"/api/v3/treatments/{id}", new JsonObject { ["insulin"] = 5.0, ["carbs"] = 55 }),
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        };

    private static async Task<HttpResponseMessage> RemoveCarbsAsync(
        HttpClient client, MealEdit edit, string id, DateTimeOffset at)
    {
        var edited = Edited(id, at);
        edited["eventType"] = "Correction Bolus";
        edited.Remove("carbs");
        return edit switch
        {
            MealEdit.V1Put => await client.PutAsJsonAsync($"/api/v1/treatments/{id}", edited),
            MealEdit.V1PutCollection => await client.PutAsJsonAsync("/api/v1/treatments", edited),
            MealEdit.V3Put => await client.PutAsJsonAsync($"/api/v3/treatments/{id}", edited),
            MealEdit.V3Patch => await client.PatchAsJsonAsync($"/api/v3/treatments/{id}", new JsonObject { ["carbs"] = 0 }),
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        };
    }

    public static TheoryData<MealOrigin, MealEdit> MealEdits()
    {
        var data = new TheoryData<MealOrigin, MealEdit>();
        foreach (var origin in Enum.GetValues<MealOrigin>())
            foreach (var edit in Enum.GetValues<MealEdit>())
                data.Add(origin, edit);
        return data;
    }

    [Theory]
    [MemberData(nameof(MealEdits))]
    public async Task EditingAMeal_UpdatesBothHalves_AndLeavesNoCarbBehind(MealOrigin origin, MealEdit edit)
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        var id = await StoreMealAsync(origin, at);

        var response = await EditAsync(client, edit, id, at);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        var meal = (await TreatmentsAtAsync(at)).Should().ContainSingle().Which;
        meal.EventType.Should().Be("Meal Bolus");
        meal.Id.Should().Be(id);
        meal.Insulin.Should().Be(5.0);
        meal.Carbs.Should().Be(55);

        var carbs = await WithTenantScopeAsync(sp => sp.GetRequiredService<ICarbIntakeRepository>()
            .GetAsync(at.UtcDateTime, at.UtcDateTime.AddMilliseconds(1), device: null, source: null, limit: 10, offset: 0, descending: true));
        var carb = carbs.Should().ContainSingle().Which;
        if (origin == MealOrigin.GlookoPair)
        {
            carb.LegacyId.Should().StartWith("glooko_carbs_", "the connector re-syncs the carbs by their own id");
            carb.SyncIdentifier.Should().Be(carb.LegacyId);
        }
    }

    [Theory]
    [MemberData(nameof(MealEdits))]
    public async Task EditingTheCarbsOutOfAMeal_RemovesItsCarbHalf(MealOrigin origin, MealEdit edit)
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        var id = await StoreMealAsync(origin, at);
        var carbsKey = (await WithTenantScopeAsync(sp => sp.GetRequiredService<ICarbIntakeRepository>()
            .GetAsync(at.UtcDateTime, at.UtcDateTime.AddMilliseconds(1), device: null, source: null, limit: 10, offset: 0, descending: true)))
            .Single().LegacyId;

        var response = await RemoveCarbsAsync(client, edit, id, at);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        var bolus = (await TreatmentsAtAsync(at)).Should().ContainSingle().Which;
        bolus.Id.Should().Be(id);
        bolus.Insulin.Should().Be(edit == MealEdit.V3Patch ? 4.0 : 5.0);
        bolus.Carbs.GetValueOrDefault().Should().Be(0);

        var carbs = await WithTenantScopeAsync(sp => sp.GetRequiredService<ICarbIntakeRepository>()
            .GetAsync(at.UtcDateTime, at.UtcDateTime.AddMilliseconds(1), device: null, source: null, limit: 10, offset: 0, descending: true));
        carbs.Should().BeEmpty();
        if (origin == MealOrigin.GlookoPair)
        {
            (await WithTenantScopeAsync(sp => sp.GetRequiredService<ICarbIntakeRepository>().IsDeletedByUserAsync(carbsKey!)))
                .Should().BeTrue("the connector must not re-sync carbs the user removed");
        }
    }

    [Theory]
    [InlineData(MealOrigin.NativeMeal, "v1")]
    [InlineData(MealOrigin.NativeMeal, "v3")]
    [InlineData(MealOrigin.GlookoPair, "v1")]
    [InlineData(MealOrigin.GlookoPair, "v3")]
    public async Task DeletingAMeal_RemovesBothHalves(MealOrigin origin, string version)
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        var id = await StoreMealAsync(origin, at);

        var delete = await client.DeleteAsync($"/api/{version}/treatments/{id}");

        delete.IsSuccessStatusCode.Should().BeTrue(await delete.Content.ReadAsStringAsync());
        (await TreatmentsAtAsync(at)).Should().BeEmpty();
    }

    #endregion

    [Theory]
    [InlineData("/api/v1/treatments", true)]
    [InlineData("/api/v1/treatments", false)]
    [InlineData("/api/v1/treatments/{id}", true)]
    public async Task APreBolusMealPostedThenPutAsOneRecord_HoldsOneCarbIntakeAtTheOffset(string route, bool withId)
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime().AddHours(-3 - Interlocked.Increment(ref _mealHour));
        var id = Guid.NewGuid().ToString("N")[..24];
        var meal = new Dictionary<string, object>
        {
            ["eventType"] = "Meal Bolus",
            ["created_at"] = Iso(at),
            ["carbs"] = 30,
            ["insulin"] = 2.0,
            ["preBolus"] = 15,
        };
        if (withId)
            meal["_id"] = id;

        (await client.PostAsJsonAsync("/api/v1/treatments", new[] { meal })).StatusCode.Should().Be(HttpStatusCode.OK);
        var put = await client.PutAsJsonAsync(route.Replace("{id}", id), meal);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var (boluses, carbs) = await WithTenantScopeAsync(async sp => (
            await sp.GetRequiredService<IBolusRepository>().GetAsync(
                at.UtcDateTime.AddMinutes(-1), at.UtcDateTime.AddMinutes(30), device: null, source: null),
            await sp.GetRequiredService<ICarbIntakeRepository>().GetAsync(
                at.UtcDateTime.AddMinutes(-1), at.UtcDateTime.AddMinutes(30), device: null, source: null,
                limit: 10, offset: 0, descending: true)));
        boluses.Should().ContainSingle().Which.Timestamp.Should().Be(at.UtcDateTime);
        var carb = carbs.Should().ContainSingle("one meal holds one carb intake").Which;
        (carb.Timestamp, carb.Carbs).Should().Be((at.UtcDateTime.AddMinutes(15), 30.0));
    }
}
