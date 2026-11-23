using FluentAssertions;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.API.Validators.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

/// <summary>
/// A V4 request validator never admits a string longer than the column it is stored in, so the
/// value is refused as a 400 instead of failing the write. A column marked
/// <see cref="FitToColumnAttribute"/> is exempt: it is cut to length on save, so a longer value
/// is stored rather than refused.
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
    };

    private static readonly Dictionary<string, string> NotStoredVerbatim = new()
    {
        ["UpsertSensorGlucoseRequest.GlucoseProcessing"] = "parsed to an enum and stored as its name, so no value can overflow the column",
    };

    private static readonly string[] RequestVerbs = ["Create", "Update", "Upsert"];

    public static TheoryData<Type> Validators()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(CreateBolusRequestValidator).Assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsClass: true } && t.Namespace == typeof(CreateBolusRequestValidator).Namespace)
                     .Where(t => typeof(IValidator).IsAssignableFrom(t))
                     .OrderBy(t => t.Name))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Validators))]
    public void A_string_stored_in_a_bounded_column_that_refuses_a_longer_value_has_a_cap_within_it(Type validatorType)
    {
        var validator = (IValidator)Activator.CreateInstance(validatorType)!;
        var entityTypes = EntityTypesFor(validatorType);
        entityTypes.Should().NotBeEmpty($"{validatorType.Name} must name the entities its request is stored in");

        var caps = validator.CreateDescriptor().GetMembersWithValidators().ToDictionary(
            group => group.Key,
            group => group.Select(g => g.Validator).OfType<ILengthValidator>().Select(l => l.Max).Where(max => max > 0).ToList());

        var violations = new List<string>();
        var requestType = RequestTypeOf(validatorType);
        foreach (var property in requestType.GetProperties()
                     .Where(p => p.PropertyType == typeof(string) && !NotStoredVerbatim.ContainsKey($"{requestType.Name}.{p.Name}")))
        {
            var cap = caps.GetValueOrDefault(property.Name) is { Count: > 0 } found ? found.Min() : (int?)null;
            foreach (var entityType in entityTypes)
            {
                // A property with no same-named column (a value kept in jsonb, or stored under another name) is not checked.
                var column = entityType.FindProperty(property.Name);
                if (column?.GetMaxLength() is not { } length
                    || column.PropertyInfo?.IsDefined(typeof(FitToColumnAttribute), inherit: true) == true)
                {
                    continue;
                }

                if (cap is null)
                {
                    violations.Add($"{property.Name} has no length rule, but {entityType.ClrType.Name}.{column.Name} holds {length}");
                }
                else if (cap > length)
                {
                    violations.Add($"{property.Name} is capped at {cap}, but {entityType.ClrType.Name}.{column.Name} holds {length}");
                }
            }
        }

        violations.Should().BeEmpty();
    }

    private static Type RequestTypeOf(Type validatorType) => validatorType.BaseType!.GetGenericArguments()[0];

    private static List<IEntityType> EntityTypesFor(Type validatorType)
    {
        var requestName = RequestTypeOf(validatorType).Name;
        var verb = RequestVerbs.First(v => requestName.StartsWith(v, StringComparison.Ordinal));
        var stem = requestName[verb.Length..^"Request".Length];
        var entityStems = EntityStemsByRequestStem.GetValueOrDefault(stem, [stem]);

        return [.. Model.GetEntityTypes().Where(e => entityStems.Any(s => e.ClrType.Name == s + "Entity"))];
    }
}
