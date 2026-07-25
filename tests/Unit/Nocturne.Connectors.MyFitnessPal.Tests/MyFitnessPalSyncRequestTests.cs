using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Connectors.MyFitnessPal.Services;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests;

/// <summary>
///     Covers the wire contract of the <c>batchSync</c> request and response, which was
///     validated field by field against the live production graph.
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
    public void BuildVariables_ReadsBackwardsFromTheNewestEntry()
    {
        var resource = FoodResource(Serialize(MyFitnessPalConnectorService.BuildVariables(null)));

        // The window is read newest-first, so the walk stops as soon as it is covered rather
        // than paging forward through years of history.
        resource.GetProperty("paginationInput").GetProperty("last").GetInt32()
            .Should().Be(MyFitnessPalConstants.PageSize);
        resource.GetProperty("paginationInput").TryGetProperty("first", out _).Should().BeFalse();
        resource.GetProperty("paginationInput").TryGetProperty("before", out _).Should().BeFalse();

        // syncCursors is non-optional on SyncResourceInput even when empty; sending a sync cursor
        // would turn the feed into a delta, which cannot reconcile against whole-day meal totals.
        resource.GetProperty("syncCursors").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void BuildVariables_PagesFurtherBackWithBefore()
    {
        var resource = FoodResource(Serialize(MyFitnessPalConnectorService.BuildVariables("page-2")));

        resource.GetProperty("paginationInput").GetProperty("before").GetString().Should().Be("page-2");
        resource.GetProperty("syncCursors").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void Response_DeserializesAliasedConnection()
    {
        const string json = """
        {
          "data": {
            "batchSync": {
              "foodDiaryEntries": {
                "edges": [
                  {
                    "node": {
                      "__typename": "ActiveFoodDiaryEntry",
                      "id": "entry-1",
                      "createdAt": "2026-07-20T09:15:00Z",
                      "date": "2026-07-20",
                      "consumedAt": "2026-07-20T08:30:00Z",
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
                        "nutrientSet": { "calories": 90, "protein": 4, "totalCarbohydrates": 15, "fat": 1 },
                        "servingSizes": [
                          { "amount": 1, "nutritionMultiplier": 1, "isFraction": false, "unit": "slice" }
                        ]
                      },
                      "consumedNutrientSet": { "calories": 180, "protein": 8, "totalCarbohydrates": 30, "fat": 2 }
                    },
                    "syncEdgeInfo": {
                      "operation": "UPSERT",
                      "lastModifiedAt": "2026-07-20T09:15:00Z"
                    }
                  }
                ],
                "pageInfo": {
                  "hasPreviousPage": false,
                  "hasNextPage": true,
                  "startCursor": "page-1",
                  "endCursor": "page-2"
                },
                "syncConnectionInfo": {
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
        node.Quantity.Should().Be(2);
        node.ServingSize!.Unit.Should().Be("slice");
        node.Food!.Brand.Should().Be("Helga");
        node.ConsumedNutrientSet!.Carbs.Should().Be(30);
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
