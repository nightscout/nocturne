using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Connectors.MyFitnessPal.Services;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests;

/// <summary>
///     Covers the wire contract of the <c>batchSync</c> request and response, which was
///     reconstructed from the MyFitnessPal mobile client.
/// </summary>
public class MyFitnessPalSyncRequestTests
{
    private static JsonElement Serialize(Dictionary<string, object?> variables) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(variables));

    private static JsonElement FoodResource(JsonElement variables) =>
        variables
            .GetProperty("input")
            .GetProperty("syncResources")
            .GetProperty("foodDiaryEntrySyncResource");

    [Fact]
    public void BuildVariables_OmitsCursors_OnFirstSync()
    {
        var resource = FoodResource(Serialize(MyFitnessPalConnectorService.BuildVariables(null, null)));

        // syncCursors is non-optional on SyncResourceInput even when both members are absent.
        resource.GetProperty("syncCursors").EnumerateObject().Should().BeEmpty();
        resource.GetProperty("paginationInput").TryGetProperty("after", out _).Should().BeFalse();
        resource.GetProperty("paginationInput").GetProperty("first").GetInt32()
            .Should().Be(MyFitnessPalConstants.PageSize);
    }

    [Fact]
    public void BuildVariables_SendsSyncCursor_AsStartAfter()
    {
        var resource = FoodResource(Serialize(MyFitnessPalConnectorService.BuildVariables("sync-1", null)));

        resource.GetProperty("syncCursors").GetProperty("startAfterSyncCursor").GetString()
            .Should().Be("sync-1");
        resource.GetProperty("syncCursors").TryGetProperty("endOnSyncCursor", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildVariables_SendsPageCursor_AsAfter()
    {
        var resource = FoodResource(Serialize(MyFitnessPalConnectorService.BuildVariables("sync-1", "page-2")));

        resource.GetProperty("paginationInput").GetProperty("after").GetString().Should().Be("page-2");
    }

    [Fact]
    public void Response_DeserializesAliasedConnection()
    {
        const string json = """
        {
          "data": {
            "batchSync": {
              "foodDiaryEntryConnection": {
                "foodDiaryEntryEdges": [
                  {
                    "foodDiaryEntryNode": {
                      "__typename": "ActiveFoodDiaryEntry",
                      "id": "entry-1",
                      "createdAt": "2026-07-20T09:15:00Z",
                      "date": "2026-07-20",
                      "consumedAt": "2026-07-20T08:30:00Z",
                      "eatingOccasion": "Breakfast",
                      "eatingOccasionSlot": 0,
                      "loggedAt": "2026-07-20T09:15:00Z",
                      "quantity": 2,
                      "servingSize": {
                        "amount": 1,
                        "nutritionMultiplier": 1,
                        "isFraction": false,
                        "unit": "slice"
                      },
                      "food": {
                        "__typename": "IndividualFood",
                        "id": "food-1",
                        "description": "Wholemeal Bread",
                        "brand": "Helga",
                        "isVerified": true,
                        "grams": 40,
                        "nutrientSet": { "calories": 90, "protein": 4, "carbs": 15, "fat": 1 },
                        "servingSizes": [
                          { "amount": 1, "nutritionMultiplier": 1, "isFraction": false, "unit": "slice" }
                        ]
                      },
                      "consumedNutrientSet": { "calories": 180, "protein": 8, "carbs": 30, "fat": 2 }
                    },
                    "foodDiaryEntryEdgeSync": {
                      "operation": "UPSERT",
                      "lastModifiedAt": "2026-07-20T09:15:00Z"
                    }
                  }
                ],
                "foodDiaryEntryPaging": {
                  "hasPreviousPage": false,
                  "hasNextPage": true,
                  "startCursor": "page-1",
                  "endCursor": "page-2"
                },
                "foodDiaryEntrySyncInfo": {
                  "startSyncCursor": "sync-0",
                  "endSyncCursor": "sync-9",
                  "totalEdges": 1
                }
              }
            }
          }
        }
        """;

        var parsed = JsonSerializer.Deserialize<MfpGraphQlResponse<MfpBatchSyncData>>(json);
        var connection = parsed?.Data?.BatchSync?.FoodDiaryEntryConnection;

        connection.Should().NotBeNull();
        connection!.FoodDiaryEntryPaging!.HasNextPage.Should().BeTrue();
        connection.FoodDiaryEntryPaging.EndCursor.Should().Be("page-2");
        connection.FoodDiaryEntrySyncInfo!.EndSyncCursor.Should().Be("sync-9");

        var edge = connection.FoodDiaryEntryEdges.Should().ContainSingle().Subject;
        edge.FoodDiaryEntryEdgeSync!.Operation.Should().Be("UPSERT");

        var node = edge.FoodDiaryEntryNode!;
        node.Id.Should().Be("entry-1");
        node.Date.Should().Be("2026-07-20");
        node.EatingOccasion.Should().Be("Breakfast");
        node.Quantity.Should().Be(2);
        node.ServingSize!.Unit.Should().Be("slice");
        node.Food!.Brand.Should().Be("Helga");
        node.ConsumedNutrientSet!.Carbs.Should().Be(30);
    }

    [Fact]
    public void WriteSecret_MergesIntoTheExistingDocument()
    {
        // Secrets are stored as one document, so a runtime write must not drop the credentials
        // the user configured.
        var stored = new Dictionary<string, string> { ["password"] = "pw", ["syncCursor"] = "old" };

        MyFitnessPalConnectorService.WriteSecret(stored, "syncCursor", "new").Should().BeTrue();
        MyFitnessPalConnectorService.WriteSecret(stored, "refreshToken", "rt").Should().BeTrue();

        stored.Should().Contain("password", "pw");
        stored.Should().Contain("syncCursor", "new");
        stored.Should().Contain("refreshToken", "rt");
    }

    [Fact]
    public void WriteSecret_RemovesTheKey_WhenTheValueIsCleared()
    {
        var stored = new Dictionary<string, string> { ["pageCursor"] = "page-7" };

        MyFitnessPalConnectorService.WriteSecret(stored, "pageCursor", null).Should().BeTrue();

        stored.Should().NotContainKey("pageCursor");
    }

    [Fact]
    public void WriteSecret_ReportsNoChange_WhenTheValueIsUnchanged()
    {
        var stored = new Dictionary<string, string> { ["syncCursor"] = "same" };

        MyFitnessPalConnectorService.WriteSecret(stored, "syncCursor", "same").Should().BeFalse();
        MyFitnessPalConnectorService.WriteSecret(stored, "pageCursor", null).Should().BeFalse();
    }

    [Fact]
    public void Response_SurfacesGraphQlErrors()
    {
        const string json = """
        { "errors": [ { "message": "Unauthorized" } ], "data": null }
        """;

        var parsed = JsonSerializer.Deserialize<MfpGraphQlResponse<MfpBatchSyncData>>(json);

        parsed!.Data.Should().BeNull();
        parsed.Errors.Should().ContainSingle().Which.Message.Should().Be("Unauthorized");
    }
}
