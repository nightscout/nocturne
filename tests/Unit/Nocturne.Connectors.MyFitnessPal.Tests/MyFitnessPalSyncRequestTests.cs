using System.Globalization;
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
    public void IsFullWalkDue_WalksWhenNoFullWalkHasEverCompleted()
    {
        // Until one completes, the connector has never been able to withdraw anything.
        MyFitnessPalConnectorService.IsFullWalkDue(new MyFitnessPalConnectorConfiguration())
            .Should().BeTrue();

        MyFitnessPalConnectorService.IsFullWalkDue(
            new MyFitnessPalConnectorConfiguration { LastFullWalkAt = "not a timestamp" })
            .Should().BeTrue();
    }

    [Fact]
    public void IsFullWalkDue_SkipsTheWalkUntilTheIntervalHasElapsed()
    {
        var justNow = DateTimeOffset.UtcNow.AddMinutes(-30);
        var overdue = DateTimeOffset.UtcNow - MyFitnessPalConstants.FullWalkInterval - TimeSpan.FromMinutes(1);

        MyFitnessPalConnectorService.IsFullWalkDue(new MyFitnessPalConnectorConfiguration
        {
            LastFullWalkAt = justNow.ToString("O", CultureInfo.InvariantCulture),
        }).Should().BeFalse();

        MyFitnessPalConnectorService.IsFullWalkDue(new MyFitnessPalConnectorConfiguration
        {
            LastFullWalkAt = overdue.ToString("O", CultureInfo.InvariantCulture),
        }).Should().BeTrue();
    }

    [Fact]
    public void IsFullWalkDue_WalksWhenTheStoredTimeIsInTheFuture()
    {
        // A clock moved; treating it as recent would suppress reconciliation indefinitely.
        MyFitnessPalConnectorService.IsFullWalkDue(new MyFitnessPalConnectorConfiguration
        {
            LastFullWalkAt = DateTimeOffset.UtcNow.AddDays(3).ToString("O", CultureInfo.InvariantCulture),
        }).Should().BeTrue();
    }

    [Fact]
    public void SelectDaysToName_NamesEveryDayWithinTheBudget()
    {
        var days = Enumerable.Range(1, 5).Select(d => $"2026-07-{d:00}").ToList();

        MyFitnessPalConnectorService.SelectDaysToName(days)
            .Should().BeEquivalentTo(days);
    }

    [Fact]
    public void SelectDaysToName_SpendsTheBudgetOnTheMostRecentDays()
    {
        // A LookbackDays of a year is configurable, and the registration advertises 365 historical
        // days; abandoning the whole window past the budget left every day unnamed forever.
        var days = Enumerable.Range(0, MyFitnessPalConstants.MaxDiaryDaysPerSync + 40)
            .Select(offset => new DateOnly(2026, 7, 20).AddDays(-offset).ToString("yyyy-MM-dd"))
            .ToList();

        var named = MyFitnessPalConnectorService.SelectDaysToName(days);

        named.Should().HaveCount(MyFitnessPalConstants.MaxDiaryDaysPerSync);
        named.Should().BeInDescendingOrder(StringComparer.Ordinal);
        named[0].Should().Be("2026-07-20");
        named.Should().NotContain(days[^1], "the oldest days are the ones that go unnamed");
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
