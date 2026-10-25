using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nocturne.API.Configuration;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Configuration;

/// <summary>
/// A document's <c>created_at</c> and <c>_id</c> under the options the legacy endpoints and connectors read and write with.
/// </summary>
public class DocumentCreatedAtProductionOptionsTests
{
    private const string UploadedValue = "1999-01-01T00:00:00.000Z";
    private const string StoredValue = "2020-02-02T00:00:00.000Z";

    private static readonly Dictionary<string, JsonSerializerOptions> ReadOptions = new()
    {
        ["NightscoutJsonBody"] = NightscoutJsonBody.Shared.JsonSerializerOptions,
        ["JsonDefaults"] = JsonDefaults.CaseInsensitive,
    };

    private static IEnumerable<Type> DocumentTypes() =>
        typeof(ProcessableDocumentBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ProcessableDocumentBase).IsAssignableFrom(t));

    public static TheoryData<Type> AllDocuments()
    {
        var data = new TheoryData<Type>();
        foreach (var type in DocumentTypes())
            data.Add(type);
        return data;
    }

    public static TheoryData<Type> AllIdTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in DocumentTypes())
            data.Add(type);
        data.Add(typeof(Profile));
        data.Add(typeof(Food));
        return data;
    }

    public static TheoryData<string, Type> ReadersWithExtensionData()
    {
        var data = new TheoryData<string, Type>();
        foreach (var type in DocumentTypes().Where(t =>
                     t.GetProperties().Any(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)))
        {
            foreach (var reader in ReadOptions.Keys)
                data.Add(reader, type);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllDocuments))]
    public void Served_document_has_created_at_and_no_camel_case_created_at(Type type)
    {
        var document = (ProcessableDocumentBase)Activator.CreateInstance(type)!;
        document.CreatedAt = StoredValue;

        var served = JsonNode.Parse(JsonSerializer.Serialize(document, type, NightscoutJsonOptions.Create()))!.AsObject();

        served["created_at"]!.GetValue<string>().Should().Be(StoredValue);
        served.ContainsKey("createdAt").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AllIdTypes))]
    public void Served_object_has_underscore_id_and_no_second_id(Type type)
    {
        var value = Activator.CreateInstance(type)!;
        type.GetProperty("Id")!.SetValue(value, "507f1f77bcf86cd799439011");

        var served = JsonNode.Parse(JsonSerializer.Serialize(value, type, NightscoutJsonOptions.Create()))!.AsObject();

        served["_id"]!.GetValue<string>().Should().Be("507f1f77bcf86cd799439011");
        served.ContainsKey("id").Should().BeFalse();
        served.ContainsKey("Id").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ReadersWithExtensionData))]
    public void Uploaded_created_at_extra_is_served_back_beside_created_at(string reader, Type type)
    {
        var json = $$"""{"created_at":"{{StoredValue}}","createdAt":"{{UploadedValue}}"}""";

        var read = JsonSerializer.Deserialize(json, type, ReadOptions[reader])!;
        var served = JsonNode.Parse(JsonSerializer.Serialize(read, type, NightscoutJsonOptions.Create()))!.AsObject();

        served["created_at"]!.GetValue<string>().Should().Be(StoredValue);
        served["createdAt"]!.GetValue<string>().Should().Be(UploadedValue);
    }
}
