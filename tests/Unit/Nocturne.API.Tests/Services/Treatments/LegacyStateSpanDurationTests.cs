using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// The duration a span-backed treatment is served with: the uploader's own value as sent, else the
/// span's length in whole minutes.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyStateSpanDurationTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static string Served(Treatment treatment) =>
        JsonDocument.Parse(JsonSerializer.Serialize(treatment)).RootElement.GetProperty("duration").GetRawText();

    private static Treatment ProjectSpan(StateSpanCategory category, string metadataJson, TimeSpan? length)
    {
        var table = LegacyTreatmentTables.StateSpanTables.Single(t => t.Category == category);
        return table.Project(
            new StateSpanEntity
            {
                Id = Guid.CreateVersion7(),
                Category = category.ToString(),
                State = category == StateSpanCategory.Override ? nameof(OverrideState.Custom) : "Active",
                StartTimestamp = Start,
                EndTimestamp = length is { } l ? Start + l : null,
                LegacyId = "6553c6b1a4d3c1c7e8f00001",
                MetadataJson = metadataJson,
            },
            CarbFoodIndex.Empty);
    }

    private static string OverrideMetadata(string? duration) =>
        $$"""{"treatments":true,"utcOffset":0{{(duration is null ? "" : $",\"duration\":{duration}")}}}""";

    private static string TargetMetadata(string? duration) =>
        $$"""{"utcOffset":0,"targetTop":120,"targetBottom":100{{(duration is null ? "" : $",\"duration\":{duration}")}}}""";

    private static Treatment ProfileSwitch(string? duration)
    {
        var json = $$"""{"profileName":"Rhys","percentage":100,"utcOffset":0{{(duration is null ? "" : $",\"duration\":{duration}")}}}""";
        var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        return LegacyTreatmentTables.ProjectProfileActivation(
            ProfileActivationLegacyFields.FromSpanMetadata(stored, Start));
    }

    public static TheoryData<string, Func<string?, TimeSpan?, Treatment>> Types => new()
    {
        { "override", (d, l) => ProjectSpan(StateSpanCategory.Override, OverrideMetadata(d), l) },
        { "temp target", (d, l) => ProjectSpan(StateSpanCategory.TemporaryTarget, TargetMetadata(d), l) },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void AnUploadedFractionalDurationIsServedAsUploaded(string type, Func<string?, TimeSpan?, Treatment> project)
    {
        Served(project("4.962561716636022", TimeSpan.FromMinutes(5))).Should().Be("4.962561716636022", type);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void AnUploadedWholeMinuteDurationIsServedWhole(string type, Func<string?, TimeSpan?, Treatment> project)
    {
        Served(project("30.0", TimeSpan.FromMinutes(30))).Should().Be("30", type);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void ADurationComputedFromTheSpanIsServedRounded(string type, Func<string?, TimeSpan?, Treatment> project)
    {
        Served(project(null, TimeSpan.FromMinutes(4.962561716636022))).Should().Be("5", type);
    }

    [Fact]
    public void AProfileSwitchServesItsUploadedFractionalDuration()
    {
        Served(ProfileSwitch("4.962561716636022")).Should().Be("4.962561716636022");
    }

    [Fact]
    public void AProfileSwitchServesAnUploadedWholeMinuteDurationWhole()
    {
        Served(ProfileSwitch("30.0")).Should().Be("30");
    }
}
