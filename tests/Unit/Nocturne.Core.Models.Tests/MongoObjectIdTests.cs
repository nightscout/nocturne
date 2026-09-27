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

    [Fact]
    public void TryGetOwnIdRange_ACanonicalUuidNamesOnlyThatUuid()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");

        MongoObjectId.TryGetOwnIdRange(id.ToString(), out var low, out var high).Should().BeTrue();

        low.Should().Be(id);
        high.Should().Be(id);
    }

    [Fact]
    public void TryGetOwnIdRange_TheUuidPrefixNamesTheRangeHoldingTheUuid()
    {
        var id = Guid.Parse("0192abcd-ef01-7123-8456-789abcdef012");

        MongoObjectId.TryGetOwnIdRange(MongoObjectId.FromGuid(id), out var low, out var high).Should().BeTrue();

        low.Should().Be(Guid.Parse("0192abcd-ef01-7123-8456-789a00000000"));
        high.Should().Be(Guid.Parse("0192abcd-ef01-7123-8456-789affffffff"));
        (id >= low && id <= high).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dexcom_7f3c2a91")]
    [InlineData("0192ABCD-EF01-7123-8456-789ABCDEF012")]
    [InlineData("0192abcdef0171238456789abcdef012")]
    [InlineData("{0192abcd-ef01-7123-8456-789abcdef012}")]
    [InlineData("507f1f77bcf80cd799439011")]
    public void TryGetOwnIdRange_NamesNothingForAnIdNocturneNeverPutsOnTheWire(string? id)
    {
        MongoObjectId.TryGetOwnIdRange(id, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0192abcdef0171238456789a", true)]
    [InlineData("0192abcdef0141238456789a", true)]
    [InlineData("0192abcdef0101238456789a", false)]
    [InlineData("0192abcdef0191238456789a", false)]
    [InlineData("0192abcdef0171230456789a", false)]
    [InlineData("0192abcdef017123c456789a", false)]
    [InlineData("0192ABCDEF0171238456789A", false)]
    public void IsGuidPrefixShaped_ChecksTheVersionAndVariantPositions(string id, bool expected)
    {
        MongoObjectId.IsGuidPrefixShaped(id).Should().Be(expected);
    }

    [Fact]
    public void NewObjectId_IsAnObjectIdThatNamesNoStoredUuid()
    {
        var id = MongoObjectId.NewObjectId();

        MongoObjectId.IsObjectId(id).Should().BeTrue();
        MongoObjectId.IsGuidPrefixShaped(id).Should().BeFalse();
        MongoObjectId.TryGetOwnIdRange(id, out _, out _).Should().BeFalse();
        MongoObjectId.NewObjectId().Should().NotBe(id);
    }
}

