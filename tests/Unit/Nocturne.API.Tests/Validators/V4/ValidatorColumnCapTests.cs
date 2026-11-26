using System.Reflection;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

/// <summary>
/// A V4 write body never carries a string longer than the column it is stored in unanswered: its
/// validator caps it, so the value is refused as a 400 instead of failing the write. A column marked
/// <see cref="FitToColumnAttribute"/> is exempt: it is cut to length on save, so a longer value
/// is stored rather than refused. A <c>[FromBody]</c> type of a V4 controller action is a record write when
/// it is stored in a record table (an entity that is <see cref="IWriterStamped"/>) or carries a
/// <c>DataSource</c> or <c>SyncIdentifier</c>: it then has a validator, and names the column of every
/// string it carries or is listed as not stored in one.
/// </summary>
[Trait("Category", "Unit")]
public class ValidatorColumnCapTests
{
    private static readonly IModel Model =
        new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql("Host=unused").Options).Model;

    private static readonly Dictionary<string, string[]> EntityStemsByRequestStem = new()
    {
        ["Meal"] = ["Bolus", "CarbIntake"],
        ["ReservoirReport"] = ["PumpSnapshot", "DeviceEvent"],
        ["Activity"] = ["StateSpan"],
    };

    private static readonly Dictionary<string, string> ColumnByRequestProperty = new()
    {
        ["CreateStateSpanRequest.OriginalId"] = "LegacyId",
        ["UpsertActivityRequest.Type"] = "State",
        ["UpsertActivityRequest.EnteredBy"] = "Source",
        ["UpsertHeartRateRequest.App"] = "EnteredBy",
        ["UpsertStepCountRequest.App"] = "EnteredBy",
    };

    private static readonly Dictionary<string, string> NotStoredVerbatim = new()
    {
        ["UpsertSensorGlucoseRequest.GlucoseProcessing"] = "parsed to an enum and stored as its name, so no value can overflow the column",
    };

    private static readonly Dictionary<string, string> NotARecordWrite = new()
    {
        ["Treatment"] = "the input of the statistics validate and clean endpoints, which return a result and store nothing",
        ["SensorGlucose"] = "the input of the statistics averaged-stats endpoint, which returns a result and stores nothing",
    };

    private static readonly Dictionary<string, string> NotStoredInAColumn = new()
    {
        ["BodyWeight.UpstreamIdentifier"] = "read from an upstream document and never mapped onto the body weight entity",
        ["CreateProfileActivationRequest.Notes"] = "kept in the activation's additional properties, which are jsonb",
        ["UpsertActivityRequest.Description"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.Intensity"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.Notes"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.DistanceUnits"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.EnergyUnits"] = "kept in the state span's metadata, which is jsonb",
        ["UpsertActivityRequest.Name"] = "kept in the state span's metadata, which is jsonb",
    };

    private static readonly string[] RequestVerbs = ["Create", "Update", "Upsert"];

    private static readonly Type[] ValidatorTypes = typeof(CreateBolusRequestValidator).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && t.Namespace == typeof(CreateBolusRequestValidator).Namespace)
        .Where(t => typeof(IValidator).IsAssignableFrom(t))
        .OrderBy(t => t.Name)
        .ToArray();

    public static TheoryData<Type> Validators()
    {
        var data = new TheoryData<Type>();
        foreach (var type in ValidatorTypes)
        {
            data.Add(type);
        }

        return data;
    }

    public static TheoryData<Type> Requests()
    {
        var data = new TheoryData<Type>();
        foreach (var type in WriteBodies())
        {
            data.Add(type);
        }

        return data;
    }

    private static IEnumerable<Type> WriteBodies() =>
        typeof(CreateBolusRequestValidator).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t)
                        && t.Namespace?.StartsWith("Nocturne.API.Controllers.V4", StringComparison.Ordinal) == true)
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(m => m.GetParameters())
            .Where(p => p.IsDefined(typeof(FromBodyAttribute), inherit: true))
            .Select(p => ElementTypeOf(p.ParameterType))
            .Where(t => t is { IsClass: true } && t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
            .Where(t => !NotARecordWrite.ContainsKey(t.Name))
            .Distinct()
            .Where(IsRecordWrite)
            .OrderBy(t => t.Name);

    private static bool IsRecordWrite(Type body) =>
        EntityTypesFor(body).Any(e => typeof(IWriterStamped).IsAssignableFrom(e.ClrType))
        || body.GetProperty("DataSource")?.PropertyType == typeof(string)
        || body.GetProperty("SyncIdentifier")?.PropertyType == typeof(string);

    private static Type ElementTypeOf(Type body)
    {
        if (body.IsArray)
        {
            return body.GetElementType()!;
        }

        var enumerable = body != typeof(string)
            ? body.GetInterfaces().Append(body).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            : null;
        return enumerable?.GetGenericArguments()[0] ?? body;
    }

    [Theory]
    [MemberData(nameof(Validators))]
    public void A_string_stored_in_a_bounded_column_that_refuses_a_longer_value_has_a_cap_within_it(Type validatorType)
    {
        var validator = (IValidator)Activator.CreateInstance(validatorType)!;
        var requestType = RequestTypeOf(validatorType);
        EntityTypesFor(requestType).Should().NotBeEmpty($"{validatorType.Name} must name the entities its request is stored in");

        var caps = validator.CreateDescriptor().GetMembersWithValidators().ToDictionary(
            group => group.Key,
            group => group.Select(g => g.Validator).OfType<ILengthValidator>().Select(l => l.Max).Where(max => max > 0).ToList());

        var violations = new List<string>();
        foreach (var (property, entityType, column, length) in BoundedColumns(requestType))
        {
            var cap = caps.GetValueOrDefault(property.Name) is { Count: > 0 } found ? found.Min() : (int?)null;
            if (cap is null)
            {
                violations.Add($"{property.Name} has no length rule, but {entityType.ClrType.Name}.{column.Name} holds {length}");
            }
            else if (cap > length)
            {
                violations.Add($"{property.Name} is capped at {cap}, but {entityType.ClrType.Name}.{column.Name} holds {length}");
            }
        }

        violations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void A_request_with_a_string_stored_in_a_bounded_column_has_a_validator(Type requestType)
    {
        EntityTypesFor(requestType).Should().NotBeEmpty($"{requestType.Name} must name the entities it is stored in");

        var bounded = BoundedColumns(requestType)
            .Select(c => $"{c.Property.Name} ({c.EntityType.ClrType.Name}.{c.Column.Name} holds {c.Length})")
            .ToList();
        if (bounded.Count == 0)
        {
            return;
        }

        ValidatorTypes.Select(RequestTypeOf).Contains(requestType).Should().BeTrue(
            $"{requestType.Name} stores {string.Join(", ", bounded)} and no validator caps it, so a longer value fails the write as a 500");
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Every_string_of_a_write_body_names_the_column_it_is_stored_in(Type requestType)
    {
        var entityTypes = EntityTypesFor(requestType);
        entityTypes.Should().NotBeEmpty($"{requestType.Name} must name the entities it is stored in");

        var unplaced = requestType.GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => !NotStoredVerbatim.ContainsKey($"{requestType.Name}.{p.Name}") && !NotStoredInAColumn.ContainsKey($"{requestType.Name}.{p.Name}"))
            .Where(p => entityTypes.All(e => e.FindProperty(ColumnNameOf(requestType, p)) is null))
            .Select(p => p.Name);

        unplaced.Should().BeEmpty(
            $"each string of {requestType.Name} must match a column of its entities, map to one in {nameof(ColumnByRequestProperty)}, or be listed in {nameof(NotStoredInAColumn)}");
    }

    private static Type RequestTypeOf(Type validatorType) => validatorType.BaseType!.GetGenericArguments()[0];

    private static IEnumerable<(PropertyInfo Property, IEntityType EntityType, IProperty Column, int Length)> BoundedColumns(Type requestType)
    {
        var entityTypes = EntityTypesFor(requestType);
        foreach (var property in requestType.GetProperties()
                     .Where(p => p.PropertyType == typeof(string) && !NotStoredVerbatim.ContainsKey($"{requestType.Name}.{p.Name}")))
        {
            var columnName = ColumnNameOf(requestType, property);
            foreach (var entityType in entityTypes)
            {
                // A property with no same-named column (a value kept in jsonb, or stored under another name) is not checked.
                var column = entityType.FindProperty(columnName);
                if (column?.GetMaxLength() is not { } length
                    || column.PropertyInfo?.IsDefined(typeof(FitToColumnAttribute), inherit: true) == true)
                {
                    continue;
                }

                yield return (property, entityType, column, length);
            }
        }
    }

    private static string ColumnNameOf(Type requestType, PropertyInfo property) =>
        ColumnByRequestProperty.GetValueOrDefault($"{requestType.Name}.{property.Name}", property.Name);

    private static List<IEntityType> EntityTypesFor(Type requestType)
    {
        var verb = RequestVerbs.FirstOrDefault(v => requestType.Name.StartsWith(v, StringComparison.Ordinal) && requestType.Name.EndsWith("Request", StringComparison.Ordinal));
        var stem = verb is null ? requestType.Name : requestType.Name[verb.Length..^"Request".Length];
        var entityStems = EntityStemsByRequestStem.GetValueOrDefault(stem, [stem]);

        return [.. Model.GetEntityTypes().Where(e => entityStems.Any(s => e.ClrType.Name == s + "Entity"))];
    }
}
