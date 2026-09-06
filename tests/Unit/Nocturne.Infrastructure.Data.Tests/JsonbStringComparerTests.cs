using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins the jsonb round-trip fix: Postgres normalizes jsonb on write (key order, whitespace), so
/// a value read back never byte-matches the app's compact serialization. <see cref="JsonbStringComparer"/>
/// must make EF change tracking treat such round-trips as unmodified — otherwise every connector
/// re-sync issues a no-op UPDATE and a false "updated" broadcast for every jsonb-backed row.
/// The model builds offline against the Npgsql provider (no connection needed for change tracking).
/// </summary>
[Trait("Category", "Unit")]
public class JsonbStringComparerTests
{
    // Compact C# serialization vs the same content as Postgres jsonb prints it
    // (space after colon, reordered keys).
    private const string CompactJson = """[{"Time":"00:00","Value":1.5,"TimeAsSeconds":0}]""";
    private const string JsonbNormalizedJson = """[{"Time": "00:00", "Value": 1.5, "TimeAsSeconds": 0}]""";

    private static NocturneDbContext NewContext() => OfflineDbContext.Create();

    [Fact]
    public void EveryJsonbStringColumn_UsesTheSemanticComparer()
    {
        using var ctx = NewContext();

        var jsonbStringProps = ctx.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.ClrType == typeof(string) && p.GetColumnType() == "jsonb")
            .ToList();

        jsonbStringProps.Should().NotBeEmpty("the model maps many string properties to jsonb columns");
        jsonbStringProps.Should().OnlyContain(
            p => p.GetValueComparer() is JsonbStringComparer,
            "every jsonb-backed string must be compared semantically, not byte-wise");
    }

    [Fact]
    public void JsonbNormalizedRoundTrip_IsNotAModification_AndNotMaterial()
    {
        using var ctx = NewContext();
        var entity = TrackedSchedule(ctx, CompactJson);

        // The mapper re-assigns the compact serialization over the jsonb-normalized original.
        entity.EntriesJson = JsonbNormalizedJson;
        ctx.ChangeTracker.DetectChanges();

        var entry = ctx.Entry(entity);
        entry.Property(e => e.EntriesJson).IsModified.Should().BeFalse(
            "jsonb normalization does not change the value semantically");
        entry.State.Should().Be(EntityState.Unchanged, "no UPDATE should be issued");
        V4MaterialChange.HasMaterialChange(entry).Should().BeFalse("no broadcast should fire");
    }

    [Fact]
    public void SemanticJsonChange_IsStillAModification()
    {
        using var ctx = NewContext();
        var entity = TrackedSchedule(ctx, CompactJson);

        entity.EntriesJson = """[{"Time":"00:00","Value":2.0,"TimeAsSeconds":0}]""";
        ctx.ChangeTracker.DetectChanges();

        var entry = ctx.Entry(entity);
        entry.Property(e => e.EntriesJson).IsModified.Should().BeTrue();
        V4MaterialChange.HasMaterialChange(entry).Should().BeTrue();
    }

    [Fact]
    public void NullToValue_IsAModification()
    {
        using var ctx = NewContext();
        var entity = TrackedSchedule(ctx, CompactJson);

        entity.AdditionalPropertiesJson = """{"a":1}""";
        ctx.ChangeTracker.DetectChanges();

        ctx.Entry(entity).Property(e => e.AdditionalPropertiesJson).IsModified.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"a":1,"b":2}""", """{"b": 2, "a": 1}""", true)] // key order + spacing
    [InlineData("""{"a":null}""", """{"a": null}""", true)]
    [InlineData("""{"a":1}""", """{"a":1,"b":null}""", false)] // explicit null key is a difference
    [InlineData("""{"a":1,"b":2}""", """{"a": 1.0, "b": 2}""", true)] // numeric equality, like Postgres jsonb
    [InlineData("""{"a":1,"a":2}""", """{"a": 2}""", false)] // duplicate keys must not throw
    [InlineData("not json", "not json", true)] // ordinal fast path
    [InlineData("not json", "also not json", false)] // invalid JSON falls back to ordinal
    public void Equals_ComparesParsedJson(string a, string b, bool expected)
    {
        JsonbStringComparer.Instance.Equals(a, b).Should().Be(expected);
    }

    private static BasalScheduleEntity TrackedSchedule(NocturneDbContext ctx, string entriesJson)
    {
        var entity = new BasalScheduleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = ctx.TenantId,
            Timestamp = DateTime.UtcNow,
            ProfileName = "Default",
            EntriesJson = entriesJson,
            AdditionalPropertiesJson = null,
        };
        ctx.Attach(entity);
        return entity;
    }
}
