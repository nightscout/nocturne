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
    public void Defaults_are_one_entry_per_renderable_widget()
    {
        var expected = WidgetCatalog.All.Where(d => d.Renderable).ToList();

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
    public void Fresh_feature_settings_use_the_catalogue_defaults()
    {
        new FeatureSettings().Widgets.Should().BeEquivalentTo(WidgetCatalog.Defaults());
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
