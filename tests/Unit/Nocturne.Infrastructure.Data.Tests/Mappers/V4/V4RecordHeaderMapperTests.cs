using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

/// <summary>
/// Every V4 mapper copies its record header through one shared helper, so these hold all of them
/// to the same contract at once.
/// </summary>
[Trait("Category", "Unit")]
public class V4RecordHeaderMapperTests
{
    public static TheoryData<string> V4EntityTypeNames()
    {
        var data = new TheoryData<string>();
        foreach (var entityType in V4EntityTypes())
        {
            data.Add(entityType.Name);
        }

        return data;
    }

    [Fact]
    public void EveryV4TimeSeriesEntityIsPairedWithAModelAndAMapper()
    {
        var entityTypes = V4EntityTypes().ToList();

        entityTypes.Should().HaveCountGreaterThanOrEqualTo(18,
            "an empty or shrunken discovery would make every theory below vacuous");
        entityTypes.Should().OnlyContain(t => ModelType(t) != null && MapperType(t) != null);
    }

    [Theory]
    [MemberData(nameof(V4EntityTypeNames))]
    public void ToDomainModel_MalformedAdditionalPropertiesJson_YieldsNoAdditionalProperties(string entityTypeName)
    {
        var entityType = EntityType(entityTypeName);
        var entity = NewEntity(entityType);
        entity.AdditionalPropertiesJson = """{"unterminated":""";

        var model = ToDomainModel(entityType, entity);

        model.AdditionalProperties.Should().BeNull(
            "one unparseable jsonb row must not fail the read of every record around it");
    }

    [Theory]
    [MemberData(nameof(V4EntityTypeNames))]
    public void HeaderRoundTripsThroughTheEntityAndBack(string entityTypeName)
    {
        var entityType = EntityType(entityTypeName);
        var model = NewModel(entityType);
        model.Id = Guid.CreateVersion7();
        model.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime;
        model.UtcOffset = -300;
        model.Device = "dexcom";
        model.App = "xdrip";
        model.DataSource = "nightscout";
        model.CorrelationId = Guid.NewGuid();
        model.LegacyId = "abc123";
        model.AdditionalProperties = new Dictionary<string, object?> { ["extra"] = "kept" };

        var roundTripped = ToDomainModel(entityType, ToEntity(entityType, model));

        roundTripped.Id.Should().Be(model.Id);
        roundTripped.Timestamp.Should().Be(model.Timestamp);
        roundTripped.Mills.Should().Be(1_700_000_000_000);
        roundTripped.UtcOffset.Should().Be(model.UtcOffset);
        roundTripped.Device.Should().Be(model.Device);
        roundTripped.App.Should().Be(model.App);
        roundTripped.DataSource.Should().Be(model.DataSource);
        roundTripped.CorrelationId.Should().Be(model.CorrelationId);
        roundTripped.LegacyId.Should().Be(model.LegacyId);
        roundTripped.AdditionalProperties.Should().ContainKey("extra");
        roundTripped.AdditionalProperties!["extra"]!.ToString().Should().Be("kept");
    }

    [Theory]
    [MemberData(nameof(V4EntityTypeNames))]
    public void ToEntity_EmptyModelId_MintsOne(string entityTypeName)
    {
        var entityType = EntityType(entityTypeName);

        var entity = ToEntity(entityType, NewModel(entityType));

        entity.Id.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(V4EntityTypeNames))]
    public void ToEntity_EmptyAdditionalProperties_WritesNoJson(string entityTypeName)
    {
        var entityType = EntityType(entityTypeName);
        var model = NewModel(entityType);
        model.AdditionalProperties = new Dictionary<string, object?>();

        ToEntity(entityType, model).AdditionalPropertiesJson.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(V4EntityTypeNames))]
    public void UpdateEntity_LeavesTheIdAndTheSystemStampsAlone(string entityTypeName)
    {
        var entityType = EntityType(entityTypeName);
        var stamped = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = NewEntity(entityType);
        entity.Id = Guid.CreateVersion7();
        entity.SysCreatedAt = stamped;
        entity.SysUpdatedAt = stamped;
        var originalId = entity.Id;

        UpdateEntity(entityType, entity, NewModel(entityType));

        entity.Id.Should().Be(originalId);
        entity.SysCreatedAt.Should().Be(stamped);
        entity.SysUpdatedAt.Should().Be(stamped);
    }

    [Theory]
    [MemberData(nameof(V4EntityTypeNames))]
    public void UpdateEntity_CopiesTheHeaderFromTheModel(string entityTypeName)
    {
        var entityType = EntityType(entityTypeName);
        var entity = NewEntity(entityType);
        entity.Device = "stale-device";
        entity.AdditionalPropertiesJson = """{"stale":true}""";

        var model = NewModel(entityType);
        model.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime;
        model.UtcOffset = 600;
        model.Device = "fresh-device";
        model.App = "fresh-app";
        model.DataSource = "fresh-source";
        model.CorrelationId = Guid.NewGuid();
        model.LegacyId = "fresh-legacy";
        model.AdditionalProperties = new Dictionary<string, object?> { ["fresh"] = 1 };

        UpdateEntity(entityType, entity, model);

        entity.Timestamp.Should().Be(model.Timestamp);
        entity.UtcOffset.Should().Be(model.UtcOffset);
        entity.Device.Should().Be(model.Device);
        entity.App.Should().Be(model.App);
        entity.DataSource.Should().Be(model.DataSource);
        entity.CorrelationId.Should().Be(model.CorrelationId);
        entity.LegacyId.Should().Be(model.LegacyId);
        entity.AdditionalPropertiesJson.Should().Contain("fresh");
    }

    private static IEnumerable<Type> V4EntityTypes() =>
        typeof(V4TimeSeriesEntityBase)
            .Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(V4TimeSeriesEntityBase)))
            .OrderBy(t => t.Name);

    private static Type EntityType(string name) => V4EntityTypes().Single(t => t.Name == name);

    private static string RecordName(Type entityType) => entityType.Name[..^"Entity".Length];

    private static Type? ModelType(Type entityType) =>
        typeof(V4RecordBase).Assembly.GetType($"Nocturne.Core.Models.V4.{RecordName(entityType)}");

    private static Type? MapperType(Type entityType) =>
        typeof(SensorGlucoseMapper).Assembly.GetType(
            $"Nocturne.Infrastructure.Data.Mappers.V4.{RecordName(entityType)}Mapper"
        );

    private static V4TimeSeriesEntityBase NewEntity(Type entityType) =>
        (V4TimeSeriesEntityBase)Activator.CreateInstance(entityType)!;

    private static V4RecordBase NewModel(Type entityType) =>
        (V4RecordBase)Activator.CreateInstance(ModelType(entityType)!)!;

    private static V4RecordBase ToDomainModel(Type entityType, V4TimeSeriesEntityBase entity) =>
        (V4RecordBase)MapperType(entityType)!.GetMethod("ToDomainModel")!.Invoke(null, [entity])!;

    private static V4TimeSeriesEntityBase ToEntity(Type entityType, V4RecordBase model) =>
        (V4TimeSeriesEntityBase)MapperType(entityType)!.GetMethod("ToEntity")!.Invoke(null, [model])!;

    private static void UpdateEntity(Type entityType, V4TimeSeriesEntityBase entity, V4RecordBase model) =>
        MapperType(entityType)!.GetMethod("UpdateEntity")!.Invoke(null, [entity, model]);
}
