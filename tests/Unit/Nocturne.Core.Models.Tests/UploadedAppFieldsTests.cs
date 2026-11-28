using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary><see cref="UploadedAppFields"/>: a span's metadata keeps and serves the uploader fields as a record does.</summary>
[Trait("Category", "Unit")]
public class UploadedAppFieldsTests
{
    private static Treatment Uploaded(
        string? enteredBy = null, string? device = null, bool? isReadOnly = null, string? app = null)
    {
        var treatment = new Treatment { EnteredBy = enteredBy, IsReadOnly = isReadOnly };
        if (device is not null)
            (treatment.AdditionalProperties ??= new())["device"] = device;
        if (app is not null)
            (treatment.AdditionalProperties ??= new())["app"] = app;
        return treatment;
    }

    private static Dictionary<string, object> Kept(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();
        UploadedAppFields.KeepInMetadata(metadata, treatment);
        return metadata;
    }

    private static Dictionary<string, object> AsStored(Dictionary<string, object> metadata) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(metadata))!;

    private static Treatment Served(IDictionary<string, object> metadata)
    {
        var treatment = new Treatment();
        UploadedAppFields.RestoreFromMetadata(treatment, metadata);
        return treatment;
    }

    private static string? ServedString(Treatment treatment, string key) =>
        KeptField.String(treatment.AdditionalProperties?.ToDictionary(p => p.Key, p => (object?)p.Value), key);

    [Fact]
    public void Metadata_keeps_each_field_only_when_sent()
    {
        Kept(Uploaded()).Should().BeEmpty();
        Kept(Uploaded(device: "Pixel")).Should().ContainKey("device").And.NotContainKey("enteredBy");
        Kept(Uploaded(enteredBy: "openaps://AAPS")).Should().ContainKey("enteredBy").And.NotContainKey("device");
        Kept(Uploaded(app: "")).Should().ContainKey("app").WhoseValue.Should().Be("");
    }

    [Fact]
    public void Metadata_keeps_an_empty_entered_by_as_a_record_does()
    {
        Kept(Uploaded(enteredBy: "")).Should().ContainKey("enteredBy").WhoseValue.Should().Be("");

        var record = UploadedAppFields.KeepUploader(null, Uploaded(enteredBy: ""));
        record.Should().ContainKey("enteredBy");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_field_is_served_as_uploaded_from_metadata_in_memory_or_read_back(bool readBack)
    {
        var metadata = Kept(Uploaded("openaps://AAPS", "Pixel", isReadOnly: true, app: "AAPS"));

        var served = Served(readBack ? AsStored(metadata) : metadata);

        served.EnteredBy.Should().Be("openaps://AAPS");
        ServedString(served, "device").Should().Be("Pixel");
        ServedString(served, "app").Should().Be("AAPS");
        served.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void A_false_read_only_is_served_false()
    {
        Served(AsStored(Kept(Uploaded(isReadOnly: false)))).IsReadOnly.Should().BeFalse();
        Served(AsStored(Kept(Uploaded()))).IsReadOnly.Should().BeNull();
    }

    [Fact]
    public void Metadata_stored_with_only_an_entered_by_serves_it_and_no_device()
    {
        var served = Served(new Dictionary<string, object> { ["enteredBy"] = "openaps://AAPS" });

        served.EnteredBy.Should().Be("openaps://AAPS");
        ServedString(served, "device").Should().BeNull();
        served.IsReadOnly.Should().BeNull();
        ServedString(served, "app").Should().BeNull();
    }

    [Fact]
    public void Metadata_with_nothing_kept_serves_nothing()
    {
        var served = Served(new Dictionary<string, object>());

        served.EnteredBy.Should().BeNull();
        served.AdditionalProperties.Should().BeNull();
    }

    [Theory]
    [InlineData("openaps://AAPS", "Pixel")]
    [InlineData("openaps://AAPS", null)]
    [InlineData(null, "Pixel")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Metadata_and_a_record_serve_the_same_uploader_fields(string? enteredBy, string? device)
    {
        var uploaded = Uploaded(enteredBy, device, isReadOnly: true);

        var fromMetadata = Served(AsStored(Kept(uploaded)));
        var fromRecord = new Treatment();
        UploadedAppFields.RestoreAll(
            fromRecord, UploadedAppFields.KeepAll(null, uploaded), app: null, unkeptEnteredBy: null);

        fromMetadata.EnteredBy.Should().Be(fromRecord.EnteredBy);
        ServedString(fromMetadata, "device").Should().Be(ServedString(fromRecord, "device"));
        fromMetadata.IsReadOnly.Should().Be(fromRecord.IsReadOnly);
    }
}
