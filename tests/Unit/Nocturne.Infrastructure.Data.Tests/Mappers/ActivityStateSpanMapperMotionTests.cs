using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// An xDrip <c>motion-class</c> record is a change of motion state, stored as a Motion span with
/// the record's class as its state, and read back through the v1 activity API as it was sent.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityStateSpanMapperMotionTests
{
    private const long At = 1_780_000_000_123;

    private static readonly string XDripRecord = $$"""
        {"type":"motion-class","timeStamp":{{At}},"created_at":"2026-05-28T20:26:40Z","class":"in vehicle"}
        """;

    private static Activity Received()
    {
        var activity = JsonSerializer.Deserialize<Activity>(XDripRecord)!;
        activity.Mills = At;
        return activity;
    }

    [Fact]
    public void ToStateSpan_stores_the_class_as_the_state_of_an_open_motion_span()
    {
        var span = ActivityStateSpanMapper.ToStateSpan(Received());

        span.Category.Should().Be(StateSpanCategory.Motion);
        span.State.Should().Be("in vehicle");
        span.StartTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(At).UtcDateTime);
        span.EndTimestamp.Should().BeNull();
    }

    [Fact]
    public void ToStateSpan_of_a_record_without_a_class_keeps_the_type_as_the_state()
    {
        var activity = Received();
        activity.AdditionalProperties!.Remove("class");

        var span = ActivityStateSpanMapper.ToStateSpan(activity);

        span.Category.Should().Be(StateSpanCategory.Motion);
        span.State.Should().Be(ActivityStateSpanMapper.MotionClassType);
    }

    [Fact]
    public void ToActivity_of_a_superseded_motion_span_returns_every_key_xdrip_sent_and_no_duration()
    {
        var span = ActivityStateSpanMapper.ToStateSpan(Received());
        span.EndTimestamp = span.StartTimestamp.AddMinutes(12);
        var stored = JsonSerializer.Deserialize<StateSpan>(JsonSerializer.Serialize(span))!;

        var read = JsonSerializer.SerializeToNode(ActivityStateSpanMapper.ToActivity(stored))!.AsObject();

        foreach (var (key, sent) in JsonNode.Parse(XDripRecord)!.AsObject())
            JsonNode.DeepEquals(read[key], sent).Should().BeTrue($"{key} is read back as it was sent");
        read["duration"].Should().BeNull();
    }
}
