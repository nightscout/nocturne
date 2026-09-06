using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// Pins every mapper's string-id-to-key derivation to the one in <see cref="MapperHelpers"/>: the
/// same legacy id must address the same row whichever collection it arrives on.
/// </summary>
[Trait("Category", "Unit")]
public class MapperIdDerivationTests
{
    private const string LegacyObjectId = "5f8d0d55b54764421b7156c3";

    [Fact]
    public void FoodMapper_DerivesTheSharedKey_ForANonGuidId()
        => FoodMapper.ToEntity(new Food { Id = LegacyObjectId, Name = "Apple" }).Id
            .Should().Be(MapperHelpers.ParseIdToGuid(LegacyObjectId));

    [Fact]
    public void SettingsMapper_DerivesTheSharedKey_ForANonGuidId()
        => SettingsMapper.ToEntity(new Settings { Id = LegacyObjectId, Key = "units" }).Id
            .Should().Be(MapperHelpers.ParseIdToGuid(LegacyObjectId));

    [Fact]
    public void FoodAndSettingsMappers_AgreeOnTheSameNonGuidId()
        => FoodMapper.ToEntity(new Food { Id = LegacyObjectId }).Id
            .Should().Be(SettingsMapper.ToEntity(new Settings { Id = LegacyObjectId }).Id);

    /// <summary>
    /// Ids that differ only past their sixteenth character collided under the truncating
    /// derivation, so a create addressed one key's row and overwrote another's.
    /// </summary>
    [Fact]
    public void SettingsMapper_SeparatesIdsSharingTheirFirstSixteenCharacters()
        => SettingsMapper.ToEntity(new Settings { Id = "settings-displayUnits", Key = "displayUnits" }).Id
            .Should().NotBe(
                SettingsMapper.ToEntity(new Settings { Id = "settings-displayRange", Key = "displayRange" }).Id);

    [Fact]
    public void Mappers_KeepAGuidIdVerbatim()
    {
        var id = Guid.CreateVersion7();

        FoodMapper.ToEntity(new Food { Id = id.ToString() }).Id.Should().Be(id);
        SettingsMapper.ToEntity(new Settings { Id = id.ToString() }).Id.Should().Be(id);
    }

    [Fact]
    public void Mappers_MintAKey_WhenNoIdIsSupplied()
    {
        FoodMapper.ToEntity(new Food { Id = null }).Id.Should().NotBe(Guid.Empty);
        SettingsMapper.ToEntity(new Settings { Id = "" }).Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>Only a 24-hex id is recorded as the addressable legacy id.</summary>
    [Fact]
    public void Mappers_RecordOnlyAMongoShapedIdAsTheOriginalId()
    {
        FoodMapper.ToEntity(new Food { Id = LegacyObjectId }).OriginalId.Should().Be(LegacyObjectId);
        FoodMapper.ToEntity(new Food { Id = "food-1A2B" }).OriginalId.Should().BeNull();
    }
}
