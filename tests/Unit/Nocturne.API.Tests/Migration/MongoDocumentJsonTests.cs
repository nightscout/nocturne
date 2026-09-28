using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using Nocturne.API.Services.Migration;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A MongoDB-mode import reads stored documents through the same models as the API path, so each
/// document must render the way Nightscout's API serves it.
/// </summary>
public class MongoDocumentJsonTests
{
    [Fact]
    public void Renders_bson_values_as_nightscout_api_json()
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            { "_id", id },
            { "created_at", new BsonDateTime(new DateTime(2026, 2, 2, 2, 40, 5, 123, DateTimeKind.Utc)) },
            { "type", "sgv" },
            { "isValid", true },
            { "sgv", 120 },
            { "date", 1770000000000L },
            { "insulin", new BsonDecimal128(1.25m) },
            { "rate", 0.8 },
            { "nan", double.NaN },
            { "inf", double.PositiveInfinity },
            { "missing", BsonNull.Value },
            { "undefined", BsonUndefined.Value },
            { "boluscalc", new BsonDocument { { "carbs", 20 }, { "at", new BsonDateTime(new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)) } } },
            { "tags", new BsonArray { "a", 2, new BsonDocument { { "x", ObjectId.Empty } } } },
        };

        var json = MigrationJob.ToNightscoutJson(document);

        json.GetProperty("_id").GetString().Should().Be(id.ToString());
        json.GetProperty("created_at").GetString().Should().Be("2026-02-02T02:40:05.123Z");
        json.GetProperty("type").GetString().Should().Be("sgv");
        json.GetProperty("isValid").GetBoolean().Should().BeTrue();
        json.GetProperty("sgv").GetInt32().Should().Be(120);
        json.GetProperty("date").GetInt64().Should().Be(1770000000000L);
        json.GetProperty("insulin").GetDecimal().Should().Be(1.25m);
        json.GetProperty("rate").GetDouble().Should().Be(0.8);
        json.GetProperty("nan").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("inf").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("undefined").ValueKind.Should().Be(JsonValueKind.Null);

        var boluscalc = json.GetProperty("boluscalc");
        boluscalc.GetProperty("carbs").GetInt32().Should().Be(20);
        boluscalc.GetProperty("at").GetString().Should().Be("2026-02-02T00:00:00.000Z");

        var tags = json.GetProperty("tags");
        tags.GetArrayLength().Should().Be(3);
        tags[0].GetString().Should().Be("a");
        tags[1].GetInt32().Should().Be(2);
        tags[2].GetProperty("x").GetString().Should().Be(ObjectId.Empty.ToString());
    }

    [Fact]
    public void A_decimal128_beyond_decimal_range_throws_overflow()
    {
        var document = new BsonDocument { { "insulin", new BsonDecimal128(Decimal128.Parse("1E+100")) } };

        var render = () => MigrationJob.ToNightscoutJson(document);

        render.Should().Throw<OverflowException>();
    }

    [Fact]
    public void A_bson_date_beyond_datetime_range_throws_argument_out_of_range()
    {
        var document = new BsonDocument { { "created_at", new BsonDateTime(long.MaxValue) } };

        var render = () => MigrationJob.ToNightscoutJson(document);

        render.Should().Throw<ArgumentOutOfRangeException>();
    }
}
