using FluentAssertions;
using Nocturne.Infrastructure.Data.Common;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// Tests that ObjectId(...) wrapper format from older Nightscout data
/// is correctly handled during mapping to database entities.
///
/// ~25% of _id values in the OpenAPS Data Commons use the wrapped format
/// "ObjectId(59dabbb7c7d5afdddbc992f4)" instead of bare "59dabbb7c7d5afdddbc992f4".
/// </summary>
public class ObjectIdCompatibilityTests
{
    // ========================================================================
    // MongoIdUtils.UnwrapObjectId — new method needed
    // ========================================================================

    [Fact]
    public void UnwrapObjectId_UnwrapsWrappedFormat()
    {
        var wrappedId = "ObjectId(59dabbb7c7d5afdddbc992f4)";

        var unwrapped = MongoIdUtils.UnwrapObjectId(wrappedId);

        unwrapped.Should().Be("59dabbb7c7d5afdddbc992f4");
        MongoIdUtils.IsValidMongoId(unwrapped).Should().BeTrue();
    }

    [Fact]
    public void UnwrapObjectId_PassesThroughBareId()
    {
        var bareId = "59dabbb7c7d5afdddbc992f4";

        var result = MongoIdUtils.UnwrapObjectId(bareId);

        result.Should().Be(bareId);
    }

    [Fact]
    public void UnwrapObjectId_HandlesNull()
    {
        MongoIdUtils.UnwrapObjectId(null).Should().BeNull();
    }

    [Fact]
    public void UnwrapObjectId_HandlesEmptyString()
    {
        MongoIdUtils.UnwrapObjectId("").Should().BeEmpty();
    }

    [Fact]
    public void UnwrapObjectId_HandlesGuidString()
    {
        var guid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        MongoIdUtils.UnwrapObjectId(guid).Should().Be(guid);
    }

}
