using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary><see cref="ProfileSpanMetadata"/>.</summary>
[Trait("Category", "Unit")]
public class ProfileSpanMetadataTests
{
    [Theory]
    [InlineData(3_600_000, "openaps://AndroidAPS", null, null)]
    [InlineData(3_600_000, null, "AAPS", null)]
    [InlineData(3_600_000, null, "aaps", null)]
    [InlineData(3_600_000, null, null, "Rhys")]
    [InlineData(0, "openaps://AndroidAPS", null, null)]
    [InlineData(-7_200_000, null, "AAPS", "Rhys")]
    public void An_AAPS_timeshift_is_milliseconds(double timeshift, string? enteredBy, string? app, string? originalProfileName)
    {
        ProfileSpanMetadata.UnitOfUploadedTimeshift(timeshift, enteredBy, app, originalProfileName)
            .Should().Be(ProfileTimeshiftUnit.Milliseconds);
    }

    [Theory]
    [InlineData(2, "careportal")]
    [InlineData(-3, null)]
    [InlineData(23, "xDrip+")]
    public void Any_other_timeshift_within_a_day_is_hours(double timeshift, string? enteredBy)
    {
        ProfileSpanMetadata.UnitOfUploadedTimeshift(timeshift, enteredBy, app: null, originalProfileName: null)
            .Should().Be(ProfileTimeshiftUnit.Hours);
    }

    [Theory]
    [InlineData(3_600_000)]
    [InlineData(-25)]
    public void An_unrecognised_uploaders_timeshift_beyond_a_day_is_milliseconds(double timeshift)
    {
        ProfileSpanMetadata.UnitOfUploadedTimeshift(timeshift, "someone", app: null, originalProfileName: null)
            .Should().Be(ProfileTimeshiftUnit.Milliseconds);
    }

    [Fact]
    public void An_AAPS_switch_names_the_profile_it_customizes_and_shifts_in_hours()
    {
        var parsed = ProfileSpanMetadata.Parse(new Dictionary<string, object>
        {
            ["profileName"] = "Rhys (110%,1h)",
            ["originalProfileName"] = "Rhys",
            ["percentage"] = 110.0,
            ["timeshift"] = 1.0,
            ["uploadedTimeshift"] = 3_600_000.0,
            ["timeshiftUnit"] = "ms",
        });

        parsed.ProfileName.Should().Be("Rhys (110%,1h)");
        parsed.BaseProfileName.Should().Be("Rhys");
        parsed.Percentage.Should().Be(110);
        parsed.TimeshiftHours.Should().Be(1);
        parsed.TimeshiftMs.Should().Be(3_600_000);
        parsed.UploadedTimeshift.Should().Be(3_600_000);
        parsed.TimeshiftUnit.Should().Be(ProfileTimeshiftUnit.Milliseconds);
        parsed.IsEffective.Should().BeFalse();
    }

    [Fact]
    public void A_span_written_before_the_unit_was_kept_infers_it_from_the_raw_value()
    {
        var parsed = ProfileSpanMetadata.Parse(new Dictionary<string, object>
        {
            ["profileName"] = "Rhys (110%,1h)",
            ["enteredBy"] = "openaps://AndroidAPS",
            ["percentage"] = 110.0,
            ["timeshift"] = 3_600_000.0,
        });

        parsed.TimeshiftHours.Should().Be(1);
        parsed.UploadedTimeshift.Should().Be(3_600_000, "it is served back as uploaded");
    }

    [Fact]
    public void A_careportal_switch_written_before_the_unit_was_kept_reads_hours()
    {
        var parsed = ProfileSpanMetadata.Parse(new Dictionary<string, object>
        {
            ["profileName"] = "Weekend",
            ["percentage"] = 90.0,
            ["timeshift"] = 2.0,
        });

        parsed.BaseProfileName.Should().Be("Weekend");
        parsed.TimeshiftHours.Should().Be(2);
        parsed.UploadedTimeshift.Should().Be(2);
    }

    [Fact]
    public void An_effective_switch_shift_is_hours_converted_from_its_original_timeshift()
    {
        var parsed = ProfileSpanMetadata.Parse(new Dictionary<string, object>
        {
            ["eventType"] = "Note",
            ["profileName"] = "Rhys (110%,1h)",
            ["originalProfileName"] = "Rhys",
            ["originalTimeshift"] = 3_600_000,
            ["timeshift"] = 1.0,
        });

        parsed.IsEffective.Should().BeTrue();
        parsed.TimeshiftHours.Should().Be(1);
        parsed.OriginalTimeshift.Should().Be(3_600_000);
        parsed.UploadedTimeshift.Should().BeNull("an effective switch uploads no timeshift of its own");
    }

    [Fact]
    public void Values_read_back_from_jsonb_parse_as_written()
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"profileName":"Rhys (90%)","originalProfileName":"Rhys","percentage":90,"timeshift":-2,"uploadedTimeshift":-7200000,"timeshiftUnit":"ms","pumpId":4102,"pumpType":"OMNIPOD_DASH","pumpSerial":"33013206","app":"AAPS"}""")!;

        var parsed = ProfileSpanMetadata.Parse(metadata);

        parsed.BaseProfileName.Should().Be("Rhys");
        parsed.TimeshiftMs.Should().Be(-7_200_000);
        parsed.UploadedTimeshift.Should().Be(-7_200_000);
        parsed.PumpId.Should().Be(4102);
        parsed.PumpType.Should().Be("OMNIPOD_DASH");
        parsed.PumpSerial.Should().Be("33013206");
        parsed.App.Should().Be("AAPS");
    }

    [Fact]
    public void A_span_with_no_metadata_parses_empty()
    {
        var parsed = ProfileSpanMetadata.Parse(null);

        parsed.BaseProfileName.Should().BeNull();
        parsed.Percentage.Should().BeNull();
        parsed.TimeshiftMs.Should().Be(0);
    }
}
