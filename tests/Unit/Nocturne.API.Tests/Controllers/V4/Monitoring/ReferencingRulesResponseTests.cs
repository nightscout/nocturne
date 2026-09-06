using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Controllers.V4.Monitoring;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Covers the 409 body <c>DELETE /api/v4/alert-rules/{id}</c> sends, for both conflict branches:
/// other rules reference the target, or the rule is managed by a source feature. The generated
/// client reads the status and the reason off the thrown value, so both have to be on the wire
/// and the reason has to read as a sentence for whichever branch it describes.
/// </summary>
[Trait("Category", "Unit")]
public class ReferencingRulesResponseTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ReferencingRulesResponse For(params Guid[] ids) => new(ids);

    [Fact]
    public void Carries_the_status_the_action_answers_with()
    {
        For(First).Status.Should().Be(StatusCodes.Status409Conflict);
        For(First, Second).Status.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public void Reads_as_one_rule_when_one_rule_refers_to_it()
    {
        For(First).Message.Should()
            .Be("Another alert rule's condition refers to this one. Update that rule first.");
    }

    [Fact]
    public void Counts_the_rules_when_more_than_one_refers_to_it()
    {
        For(First, Second).Message.Should()
            .Be("2 other alert rules' conditions refer to this one. Update those rules first.");
    }

    [Fact]
    public void Names_the_owning_feature_when_the_rule_is_managed()
    {
        new ReferencingRulesResponse([], "tracker").Message.Should()
            .Be("This rule is managed by 'tracker' — delete the tracker notification threshold instead.");
    }

    [Fact]
    public void Puts_the_status_the_reason_and_the_ids_on_the_wire()
    {
        var wire = JsonSerializer.SerializeToNode(
            For(First),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        wire["status"]!.GetValue<int>().Should().Be(StatusCodes.Status409Conflict);
        wire["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        wire["referencingRuleIds"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void Keeps_the_ids_array_on_the_wire_for_a_managed_conflict()
    {
        // Serde-strict generated clients (rust) deserialize every declared field, so the
        // referencing-ids array must be present (empty) even when the conflict is ownership.
        var wire = JsonSerializer.SerializeToNode(
            new ReferencingRulesResponse([], "tracker"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        wire["referencingRuleIds"]!.AsArray().Should().BeEmpty();
        wire["managedBy"]!.GetValue<string>().Should().Be("tracker");
    }
}
