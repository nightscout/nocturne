using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// A state span's Source carries the connector that wrote it, displacing the uploader name it used
/// to hold. Pins both directions of that displacement, including the fallbacks that keep rows
/// written before the change readable.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityStateSpanMapperSourceTests
{
    private static readonly DateTime MidJune = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static Activity Exercise() => new()
    {
        Id = "activity-1",
        Type = "exercise",
        Mills = new DateTimeOffset(MidJune).ToUnixTimeMilliseconds(),
    };

    [Fact]
    public void ToStateSpan_prefers_the_connector_over_the_uploader_name()
    {
        var activity = Exercise();
        activity.DataSource = "nightscout-connector";
        activity.EnteredBy = "xdrip";

        ActivityStateSpanMapper.ToStateSpan(activity).Source.Should().Be("nightscout-connector");
    }

    [Fact]
    public void ToStateSpan_falls_back_to_the_uploader_name_when_no_connector_wrote_it()
    {
        // The v1 activity API path: no connector is publishing, so EnteredBy stays the source.
        var activity = Exercise();
        activity.EnteredBy = "xdrip";

        ActivityStateSpanMapper.ToStateSpan(activity).Source.Should().Be("xdrip");
    }

    [Fact]
    public void ToStateSpan_falls_back_to_nightscout_when_neither_is_present()
    {
        ActivityStateSpanMapper.ToStateSpan(Exercise()).Source.Should().Be("nightscout");
    }

    [Fact]
    public void ToActivity_recovers_the_uploader_name_from_a_row_written_before_the_change()
    {
        // Old-shape row: Source IS the uploader name and metadata carries no enteredBy key. Every
        // pre-existing exercise/illness/travel row looks like this. Metadata has to be non-empty
        // or the read block is skipped wholesale and the assertion pins nothing.
        var oldShape = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = MidJune,
            Source = "xdrip",
            OriginalId = "activity-1",
            Metadata = new Dictionary<string, object> { ["notes"] = "morning walk" },
        };

        var activity = ActivityStateSpanMapper.ToActivity(oldShape)!;

        activity.Notes.Should().Be("morning walk");
        activity.EnteredBy.Should().Be("xdrip");
    }

    [Fact]
    public void ToActivity_surfaces_the_source_when_the_span_carries_no_metadata_at_all()
    {
        // A span posted through the v4 StateSpans controller can have a null metadata bag, which
        // skips the metadata read block entirely — EnteredBy has to come from the initializer.
        var noMetadata = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = MidJune,
            Source = "xdrip",
            OriginalId = "activity-1",
        };

        ActivityStateSpanMapper.ToActivity(noMetadata)!.EnteredBy.Should().Be("xdrip");
    }

    [Fact]
    public void ToActivity_prefers_the_stashed_uploader_name_over_the_connector_source()
    {
        var span = ActivityStateSpanMapper.ToStateSpan(new Activity
        {
            Id = "activity-1",
            Type = "exercise",
            Mills = new DateTimeOffset(MidJune).ToUnixTimeMilliseconds(),
            EnteredBy = "xdrip",
            DataSource = "nightscout-connector",
        });

        ActivityStateSpanMapper.ToActivity(span)!.EnteredBy.Should().Be("xdrip");
    }
}
