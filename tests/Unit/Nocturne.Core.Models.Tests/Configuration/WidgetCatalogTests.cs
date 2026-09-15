using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class WidgetCatalogTests
{
    [Fact]
    public void Catalogue_covers_every_widget_id_exactly_once()
    {
        WidgetCatalog.All.Select(d => d.Id).Should().BeEquivalentTo(Enum.GetValues<WidgetId>());
    }

    // Name and default are pinned per id: changing one is a deliberate edit here, never a silent divergence.
    [Theory]
    [InlineData(WidgetId.BgDelta, "BG Delta", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.LastUpdated, "Last Updated", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.ConnectionStatus, "Connection Status", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.Meals, "Recent Meals", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.Trackers, "Trackers", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.TirChart, "Time in Range", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.DailySummary, "Daily Summary", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.Clock, "Clock", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.Tdd, "Total Daily Dose", null, WidgetPlacement.Top, true)]
    [InlineData(WidgetId.GlucoseChart, "Glucose Chart", true, WidgetPlacement.Main, true)]
    [InlineData(WidgetId.Statistics, "Statistics", true, WidgetPlacement.Main, true)]
    [InlineData(WidgetId.Predictions, "Predictions", true, WidgetPlacement.Main, true)]
    [InlineData(WidgetId.DailyStats, "Daily Stats", true, WidgetPlacement.Main, true)]
    [InlineData(WidgetId.Treatments, "Treatments", true, WidgetPlacement.Main, true)]
    [InlineData(WidgetId.Agp, "AGP", false, WidgetPlacement.Main, false)]
    [InlineData(WidgetId.BatteryStatus, "Battery Status", false, WidgetPlacement.Main, false)]
    public void Catalogue_row_is_pinned(
        WidgetId id,
        string name,
        bool? defaultEnabled,
        WidgetPlacement placement,
        bool renderable
    )
    {
        WidgetCatalog
            .All.Single(d => d.Id == id)
            .Should()
            .BeEquivalentTo(
                new
                {
                    Name = name,
                    DefaultEnabled = defaultEnabled,
                    Placement = placement,
                    Renderable = renderable,
                }
            );
    }

    [Fact]
    public void Catalogue_rows_are_fully_described()
    {
        WidgetCatalog
            .All.Should()
            .OnlyContain(d =>
                d.Name.Length > 0 && d.Description.Length > 0 && d.Icon.Length > 0
            );
    }

    [Fact]
    public void Defaults_are_one_entry_per_renderable_widget_the_catalogue_defaults()
    {
        var expected = WidgetCatalog
            .All.Where(d => d.Renderable && d.DefaultEnabled != null)
            .ToList();

        WidgetCatalog
            .Defaults()
            .Should()
            .BeEquivalentTo(
                expected.Select(d => new
                {
                    d.Id,
                    Enabled = d.DefaultEnabled,
                    d.Placement,
                }),
                o => o.WithStrictOrdering()
            );
    }

    [Fact]
    public void Defaults_omit_the_widgets_nothing_renders()
    {
        WidgetCatalog
            .Defaults()
            .Should()
            .NotContain(w => w.Id == WidgetId.Agp || w.Id == WidgetId.BatteryStatus);
    }

    [Fact]
    public void Catalogue_leaves_the_top_grid_to_the_client()
    {
        WidgetCatalog
            .All.Where(d => d.Placement == WidgetPlacement.Top)
            .Should()
            .OnlyContain(d => d.DefaultEnabled == null);
    }

    [Fact]
    public void Fresh_feature_settings_gate_the_main_sections_only()
    {
        new FeatureSettings()
            .Widgets.Should()
            .NotBeEmpty()
            .And.OnlyContain(w => w.Placement == WidgetPlacement.Main);
    }

    [Fact]
    public void Fresh_feature_settings_use_the_catalogue_defaults()
    {
        new FeatureSettings().Widgets.Should().BeEquivalentTo(WidgetCatalog.Defaults());
    }

    [Fact]
    public void MainSectionsOf_drops_the_top_rows_settings_were_stored_with()
    {
        var stored = new List<WidgetConfig>
        {
            new() { Id = WidgetId.BgDelta, Enabled = true, Placement = WidgetPlacement.Top },
            new() { Id = WidgetId.Statistics, Enabled = false, Placement = WidgetPlacement.Main },
        };

        WidgetCatalog
            .MainSectionsOf(stored)
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new { Id = WidgetId.Statistics, Enabled = false });
    }

    [Fact]
    public void Stored_settings_naming_an_unrendered_widget_still_deserialise()
    {
        const string json = """
            {
              "widgets": [
                { "id": "Agp", "enabled": true, "placement": "Main" },
                { "id": "BatteryStatus", "enabled": true, "placement": "Main" }
              ]
            }
            """;

        var settings = JsonSerializer.Deserialize<FeatureSettings>(json)!;

        settings.Widgets.Select(w => w.Id).Should().Equal(WidgetId.Agp, WidgetId.BatteryStatus);
    }
}
