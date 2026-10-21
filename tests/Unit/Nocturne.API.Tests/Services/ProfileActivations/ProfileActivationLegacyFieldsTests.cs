using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.ProfileActivations;

/// <summary>
/// A Profile span's metadata as the activation it converts to, under the rules migration
/// <c>AddProfileActivations</c> applies in SQL, and back as the treatment it was uploaded as.
/// </summary>
public class ProfileActivationLegacyFieldsTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Metadata as a stored span holds it: every value a <see cref="JsonElement"/>.</summary>
    private static Dictionary<string, object> Stored(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);

    [Fact]
    public void AnAapsSpanWrittenBeforeTheUnitWasKeptHasItsShiftReadAsMilliseconds()
    {
        var activation = ProfileActivationLegacyFields.FromSpanMetadata(Stored(
            """{"profileName":"Rhys (110%,1h)","originalProfileName":"Rhys","percentage":110,"timeshift":3600000,"utcOffset":600,"enteredBy":"openaps://AndroidAPS"}"""),
            Start);

        activation.BaseProfileName.Should().Be("Rhys");
        activation.Percentage.Should().Be(110);
        activation.TimeshiftMs.Should().Be(3_600_000);
        activation.AdditionalProperties.Should().ContainKey(ProfileSpanMetadata.TimeshiftUnitKey).WhoseValue.Should().Be("ms");
        activation.AdditionalProperties.Should().ContainKey(ProfileSpanMetadata.UploadedTimeshiftKey);
        activation.AdditionalProperties.Should().NotContainKeys(ProfileSpanMetadata.PercentageKey, "enteredBy", "utcOffset");
        activation.EnteredBy.Should().Be("openaps://AndroidAPS");
        activation.UtcOffset.Should().Be(600);

        LegacyTreatmentTables.ProjectProfileActivation(activation).Timeshift.Should().Be(3_600_000);
    }

    [Fact]
    public void ACareportalSpanWrittenBeforeTheUnitWasKeptHasItsShiftReadAsHours()
    {
        var activation = ProfileActivationLegacyFields.FromSpanMetadata(Stored(
            """{"profileName":"Weekend","timeshift":2,"utcOffset":0,"enteredBy":"careportal"}"""), Start);

        activation.TimeshiftMs.Should().Be(7_200_000);
        activation.AdditionalProperties.Should().NotContainKey(ProfileSpanMetadata.ProfileNameKey,
            "the uploaded name is the base name, which the activation carries");
        LegacyTreatmentTables.ProjectProfileActivation(activation).Timeshift.Should().Be(2);
    }

    [Fact]
    public void ASwitchUploadedWithoutAPercentageReadsBackWithoutOne()
    {
        var omitted = ProfileActivationLegacyFields.FromSpanMetadata(Stored("""{"profileName":"Sick"}"""), Start);
        var uploaded = ProfileActivationLegacyFields.FromSpanMetadata(
            Stored("""{"profileName":"Sick","percentage":100}"""), Start);

        omitted.Percentage.Should().Be(100, "the column holds the percentage it runs at");
        ProfileActivationLegacyFields.ToSpanMetadata(omitted).Should()
            .NotContainKey(ProfileSpanMetadata.PercentageKey)
            .And.NotContainKey(ProfileActivationLegacyFields.PercentageOmittedKey);
        ProfileActivationLegacyFields.ToSpanMetadata(uploaded).Should().ContainKey(ProfileSpanMetadata.PercentageKey);
    }

    [Fact]
    public void ASpanWrittenBeforeItsLengthWasKeptReadsItFromItsOwnEnd()
    {
        var activation = ProfileActivationLegacyFields.FromSpanMetadata(
            Stored("""{"profileName":"Weekend","utcOffset":0}"""), Start, Start.AddMinutes(45), superseded: false);

        activation.RequestedDurationMs.Should().Be(45 * 60_000);
    }

    [Fact]
    public void AnEndALaterSwitchSetIsNotTheSwitchsLength()
    {
        var activation = ProfileActivationLegacyFields.FromSpanMetadata(
            Stored("""{"profileName":"Weekend","utcOffset":0}"""), Start, Start.AddMinutes(45), superseded: true);

        activation.RequestedDurationMs.Should().BeNull();
    }

    [Fact]
    public void AnEffectiveSpanKeepsOnlyWhatTheNoteIsReadBackWithBeyondItsColumns()
    {
        var activation = ProfileActivationLegacyFields.FromSpanMetadata(Stored(
            """{"eventType":"Note","profileName":"Rhys (110%)","originalProfileName":"Rhys","originalCustomizedName":"Rhys (110%)","originalPercentage":110,"percentage":110,"originalTimeshift":0,"timeshift":0,"originalDuration":0,"originalEnd":1772352000000,"notes":"Rhys (110%)","utcOffset":0}"""),
            Start);

        activation.Phase.Should().Be(ProfileActivationPhase.Effective);
        activation.SnapshotIsCustomized.Should().BeTrue();
        activation.EndTimestamp.Should().BeNull("a permanent switch's originalEnd is its own start");
        activation.AdditionalProperties!.Keys.Should().BeEquivalentTo(
            "eventType", "profileName", "originalCustomizedName", "originalDuration", "originalEnd", "notes");

        var served = LegacyTreatmentTables.ProjectProfileActivation(activation);
        served.EventType.Should().Be("Note");
        served.OriginalProfileName.Should().Be("Rhys");
        served.OriginalPercentage.Should().Be(110);
        served.OriginalCustomizedName.Should().Be("Rhys (110%)");
        served.Notes.Should().Be("Rhys (110%)");
    }

    [Fact]
    public void TheMetadataRebuiltFromAnActivationParsesAsTheSpanItCameFrom()
    {
        var metadata = Stored(
            """{"profileName":"Rhys (90%,-2h)","originalProfileName":"Rhys","percentage":90,"timeshift":-2,"uploadedTimeshift":-7200000,"timeshiftUnit":"ms","duration":30,"durationInMilliseconds":1800000,"pumpId":4102,"pumpType":"OMNIPOD_DASH","pumpSerial":"33013206","app":"AAPS","reason":"walk","utcOffset":0}""");

        var activation = ProfileActivationLegacyFields.FromSpanMetadata(metadata, Start);
        var span = ProfileSpanMetadata.Parse(ProfileActivationLegacyFields.ToSpanMetadata(activation));
        var original = ProfileSpanMetadata.Parse(metadata);

        span.Should().BeEquivalentTo(original);
        activation.RequestedDurationMs.Should().Be(1_800_000);
        activation.TimeshiftMs.Should().Be(-7_200_000);
    }
}
