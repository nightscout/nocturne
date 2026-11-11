using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>Two series of one colour and one mark on a track differ by a texture (<see cref="ReportSpecRunner.Texture"/>).</summary>
public class ReportTextureTests
{
    internal static ReportLayerResult Layer(string id, string dataset, ReportMark mark, ReportLayerBindings bindings)
    {
        var drawn = (bindings.Y ?? []).Concat(bindings.Edges ?? []).ToHashSet();
        return new ReportLayerResult
        {
            Id = id,
            Title = id,
            Mark = mark,
            Bindings = bindings,
            XKind = ReportXKind.Day,
            Side = ReportAxisSide.Left,
            Status = ReportPanelStatus.Ok,
            Window = new ReportWindow(0, 1),
            Fields = [.. ReportSpecValidatorTests.Registry.Find(dataset)!.FieldsFor(ReportPanelKind.Chart).Where(f => drawn.Contains(f.Name))],
        };
    }

    private static ReportLayerBindings Bars(params string[] y) => new() { X = "day", Y = y };

    private static string? TextureOf(ReportLayerResult layer, string field) => layer.Fields.Single(f => f.Name == field).Texture;

    [Fact]
    public void Total_insulin_and_total_bolus_as_bars_of_one_layer_differ_by_a_texture()
    {
        var layer = ReportSpecRunner.Texture(ReportSpecRunner.Hue([Layer("a", "daily_summary", ReportMark.Bars, Bars("totalInsulin", "totalBolus"))])).Single();

        layer.Color.Should().BeNull();
        layer.Fields.Select(f => f.Color).Should().AllBeEquivalentTo(ChartColor.InsulinBolus);
        TextureOf(layer, "totalInsulin").Should().BeNull();
        TextureOf(layer, "totalBolus").Should().Be("cat-1");
    }

    [Fact]
    public void Total_insulin_and_total_bolus_as_two_layers_differ_by_the_series_hue_and_need_no_texture()
    {
        var layers = ReportSpecRunner.Texture(ReportSpecRunner.Hue(
        [
            Layer("a", "daily_summary", ReportMark.Bars, Bars("totalInsulin")),
            Layer("b", "daily_summary", ReportMark.Bars, Bars("totalBolus")),
        ]));

        layers.Select(l => l.Color).Should().Equal(null, ChartColor.Profile);
        layers.SelectMany(l => l.Fields).Should().OnlyContain(f => f.Texture == null);
    }

    [Fact]
    public void Time_in_range_bars_and_the_ranges_stack_differ_by_a_texture_on_the_stacks_in_range_part()
    {
        var layers = ReportSpecRunner.Texture(ReportSpecRunner.Hue(
        [
            Layer("tir", "daily_summary", ReportMark.Bars, Bars("timeInRange")),
            Layer("ranges", "daily_summary", ReportMark.Bars, new ReportLayerBindings { X = "day", Y = ["timeBelow", "timeInRange", "timeAbove"], Stacked = true }),
        ]));

        layers.Should().OnlyContain(l => l.Color == null);
        TextureOf(layers[0], "timeInRange").Should().BeNull();
        TextureOf(layers[1], "timeInRange").Should().Be("cat-1");
        TextureOf(layers[1], "timeBelow").Should().BeNull();
        TextureOf(layers[1], "timeAbove").Should().BeNull();
    }

    [Fact]
    public void One_colour_in_two_marks_needs_no_texture()
    {
        var layers = ReportSpecRunner.Texture(
        [
            Layer("a", "daily_summary", ReportMark.Bars, Bars("totalInsulin")),
            Layer("b", "daily_summary", ReportMark.Line, Bars("totalBolus")),
        ]);

        layers.SelectMany(l => l.Fields).Should().OnlyContain(f => f.Texture == null);
    }

    [Fact]
    public void Textures_are_taken_in_order_and_cycle()
    {
        var layers = ReportSpecRunner.Texture([.. Enumerable.Range(0, 8).Select(i => Layer($"l{i}", "daily_summary", ReportMark.Bars, Bars("totalBolus")))]);

        layers.Select(l => l.Fields.Single().Texture).Should().Equal(null, "cat-1", "cat-2", "cat-3", "cat-4", "cat-5", "cat-6", "cat-1");
        ReportSpecRunner.SeriesTextures.Should().Equal("cat-1", "cat-2", "cat-3", "cat-4", "cat-5", "cat-6");
    }
}
