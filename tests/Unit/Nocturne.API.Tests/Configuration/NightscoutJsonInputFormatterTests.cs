using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Configuration;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Configuration;

/// <summary>
/// A v1-v3 body names its identity with <c>_id</c>; an uploader's own <c>id</c> stays a plain field,
/// as it does on legacy Nightscout. MVC's default case-insensitive binding would let it replace <c>_id</c>.
/// </summary>
[Trait("Category", "Unit")]
public class NightscoutJsonInputFormatterTests
{
    private const string ObjectIdA = "6ab400000000000000000001";
    private const string ObjectIdB = "6ab400000000000000000002";
    private const string UploaderId = "B5E5A1C2-0000-4000-8000-000000000001";

    private readonly NightscoutJsonInputFormatter _formatter = new(NullLogger<SystemTextJsonInputFormatter>.Instance);

    [Fact]
    public async Task A_v3_treatment_is_identified_by_its_object_id()
    {
        var context = Context<Treatment>("/api/v3/treatments", $$"""{"_id":"{{ObjectIdA}}","id":"{{UploaderId}}","carbs":10}""");

        _formatter.CanRead(context).Should().BeTrue();
        var treatment = (Treatment)(await _formatter.ReadAsync(context)).Model!;

        treatment.Id.Should().Be(ObjectIdA);
        TreatmentClientId.Of(treatment).Should().Be(UploaderId);
    }

    [Fact]
    public async Task A_v3_bulk_upload_keeps_carb_equivalents_sharing_one_uploader_id_apart()
    {
        var context = Context<Treatment[]>("/api/v3/treatments/bulk", $$"""
            [
                {"_id":"{{ObjectIdA}}","id":"{{UploaderId}}","carbs":10},
                {"_id":"{{ObjectIdB}}","id":"{{UploaderId}}","carbs":10}
            ]
            """);

        var treatments = (Treatment[])(await _formatter.ReadAsync(context)).Model!;

        treatments.Select(t => t.Id).Should().Equal(ObjectIdA, ObjectIdB);
    }

    [Fact]
    public void A_v4_body_is_left_to_the_default_formatter()
    {
        var context = Context<Treatment>("/api/v4/treatments", "{}");

        _formatter.CanRead(context).Should().BeFalse();
    }

    private static InputFormatterContext Context<T>(string path, string json)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return new InputFormatterContext(
            http,
            "body",
            new ModelStateDictionary(),
            new EmptyModelMetadataProvider().GetMetadataForType(typeof(T)),
            (stream, encoding) => new StreamReader(stream, encoding));
    }
}
