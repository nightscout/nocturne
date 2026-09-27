using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Extensions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// V1/V3 record identifiers must be 24-char hex Mongo ObjectIds — AAPS validates every id with
/// <c>isObjectId()</c> and crashes (NumberFormatException) on a UUID. The conversion must be
/// deterministic (stable across syncs) and reversible to a uuid range for lookup.
/// </summary>
[Trait("Category", "Unit")]
public class MongoObjectIdTests
{
    [Fact]
    public void FromGuid_IsFirst24HexOfCanonicalForm_AndIsObjectId()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");

        var oid = MongoObjectId.FromGuid(id);

        oid.Length.Should().Be(24);
        MongoObjectId.IsObjectId(oid).Should().BeTrue();
        id.ToString("N").Should().StartWith(oid);
    }

    [Fact]
    public void FromGuid_IsDeterministic()
    {
        var id = Guid.NewGuid();
        MongoObjectId.FromGuid(id).Should().Be(MongoObjectId.FromGuid(id));
    }

    [Theory]
    [InlineData("0192abcdef01712384560000", true)]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("0192ABCDEF01712384560000", false)] // uppercase rejected
    [InlineData("0192abcd-ef01-7123-8456-789abcdef012", false)] // full UUID
    [InlineData("507f1f77bcf86cd79943901", false)] // 23 chars
    [InlineData("syn-abc", false)]
    public void IsObjectId_MatchesStrict24Hex(string value, bool expected)
    {
        MongoObjectId.IsObjectId(value).Should().Be(expected);
    }

    [Fact]
    public void Coerce_PassesThroughRealObjectId()
    {
        MongoObjectId.Coerce("507f1f77bcf86cd799439011").Should().Be("507f1f77bcf86cd799439011");
    }

    [Fact]
    public void Coerce_ConvertsGuidToObjectId()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");
        MongoObjectId.Coerce(id.ToString()).Should().Be(MongoObjectId.FromGuid(id));
    }

    [Fact]
    public void Coerce_HashesArbitraryLegacyStringToObjectId()
    {
        var result = MongoObjectId.Coerce("syn-not-a-uuid");
        MongoObjectId.IsObjectId(result).Should().BeTrue();
        // deterministic
        MongoObjectId.Coerce("syn-not-a-uuid").Should().Be(result);
    }

    [Fact]
    public void Coerce_LeavesNullAndEmptyUnchanged()
    {
        MongoObjectId.Coerce(null).Should().BeNull();
        MongoObjectId.Coerce("").Should().Be("");
    }

    [Fact]
    public void TryGetGuidPrefixRange_BracketsTheSourceGuid()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");
        var oid = MongoObjectId.FromGuid(id);

        MongoObjectId.TryGetGuidPrefixRange(oid, out var low, out var high).Should().BeTrue();

        // The source UUID's canonical form starts with the objectId, so it sits within
        // [oid+00000000, oid+ffffffff] under hex-string (Postgres uuid) ordering.
        id.ToString("N").Should().StartWith(oid);
        low.ToString("N").Should().Be(oid + "00000000");
        high.ToString("N").Should().Be(oid + "ffffffff");
    }

    [Fact]
    public void TryGetGuidPrefixRange_RejectsNonObjectId()
    {
        MongoObjectId.TryGetGuidPrefixRange("not-an-oid", out _, out _).Should().BeFalse();
        MongoObjectId.TryGetGuidPrefixRange(Guid.NewGuid().ToString(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Treatment_SerializesIdAndIdentifierAsObjectId()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");
        var treatment = new Treatment { Id = id.ToString(), EventType = "Note" };

        var json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(treatment));

        var expected = MongoObjectId.FromGuid(id);
        json.GetProperty("_id").GetString().Should().Be(expected);
        json.GetProperty("identifier").GetString().Should().Be(expected);
    }

    [Fact]
    public void Treatment_PreservesRealObjectIdOnWire()
    {
        var treatment = new Treatment { Id = "507f1f77bcf86cd799439011", EventType = "Note" };
        var json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(treatment));
        json.GetProperty("_id").GetString().Should().Be("507f1f77bcf86cd799439011");
    }

    [Fact]
    public void EntryV3Response_SerializesIdentifierAsObjectId()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");
        var entry = new Entry { Id = id.ToString(), Mills = 1711454400000, Sgv = 120 };

        var json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(entry.ToV3Response()));

        var expected = MongoObjectId.FromGuid(id);
        json.GetProperty("_id").GetString().Should().Be(expected);
        json.GetProperty("identifier").GetString().Should().Be(expected);
    }
    /// <summary>
    /// The shape filter ahead of a uuid range lookup: an id <see cref="MongoObjectId.FromGuid"/>
    /// produced always passes, and it rejects ids whose version or variant position a UUID could not
    /// hold, as well as anything that is not an ObjectId at all.
    /// </summary>
    [Theory]
    [InlineData("0198c2a41f3b7c2d9e556a1b", true)] // v7 uuid prefix, variant 9
    [InlineData("0192abcdef0171238456789a", true)] // v7, variant 8
    [InlineData("0192abcdef014123b456789a", true)] // v4, variant b
    [InlineData("0192abcdef010123a456789a", false)] // version nibble 0
    [InlineData("0192abcdef019123a456789a", false)] // version nibble 9
    [InlineData("0192abcdef0171237456789a", false)] // variant 7 (NCS)
    [InlineData("0192abcdef017123c456789a", false)] // variant c (Microsoft)
    [InlineData("0192ABCDEF0171238456789A", false)] // uppercase is not an ObjectId
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGuidPrefixShaped_AcceptsOnlyTheUuidPrefixShape(string? value, bool expected)
    {
        MongoObjectId.IsGuidPrefixShaped(value).Should().Be(expected);
    }

    [Fact]
    public void IsGuidPrefixShaped_HoldsForEveryFromGuidResult()
    {
        for (var i = 0; i < 64; i++)
        {
            MongoObjectId.IsGuidPrefixShaped(MongoObjectId.FromGuid(Guid.CreateVersion7())).Should().BeTrue();
            MongoObjectId.IsGuidPrefixShaped(MongoObjectId.FromGuid(Guid.NewGuid())).Should().BeTrue();
        }
    }

    [Fact]
    public void NewObjectId_IsAFreshObjectIdTheWirePassesThrough()
    {
        var ids = Enumerable.Range(0, 64).Select(_ => MongoObjectId.NewObjectId()).ToList();

        ids.Should().OnlyContain(id => MongoObjectId.IsObjectId(id));
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => MongoObjectId.Coerce(id) == id);
    }

    [Fact]
    public void NewObjectId_IsNotGuidPrefixShaped()
    {
        for (var i = 0; i < 64; i++)
            MongoObjectId.IsGuidPrefixShaped(MongoObjectId.NewObjectId()).Should().BeFalse();
    }
}
