using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>Answer of <c>GET_PRODUCTS {product_type}</c>: the device catalogue, grouped by brand.</summary>
public sealed class GlookoXtProductsResponse
{
    [JsonPropertyName("brands")] public List<GlookoXtBrand>? Brands { get; set; }
}

public sealed class GlookoXtBrand
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(GlookoXtLenientLongConverter))]
    public long? Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("products")] public List<GlookoXtProduct>? Products { get; set; }
}

public sealed class GlookoXtProduct
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(GlookoXtLenientLongConverter))]
    public long? Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("model_name")] public string? ModelName { get; set; }
    [JsonPropertyName("product_type")] public string? ProductType { get; set; }
}
