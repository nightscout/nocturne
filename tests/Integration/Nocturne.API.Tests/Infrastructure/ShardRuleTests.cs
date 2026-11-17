using FluentAssertions;
using Nocturne.API.Tests.Integration.Parity;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>The rules of <see cref="ApiIntegrationShards"/> for the API integration shards.</summary>
public class ApiIntegrationShardTests()
    : ShardRuleTests<ApiIntegrationTestBase, ApiIntegrationTestFixture>(ApiIntegrationShards.All, minimumClasses: 50);

/// <summary>The rules of <see cref="ApiIntegrationShards"/> for the parity shards.</summary>
public class ParityShardTests()
    : ShardRuleTests<ParityTestBase, ParityTestFixture>(ParityShards.All, minimumClasses: 20);

/// <summary>
/// The rules of <see cref="ApiIntegrationShards"/>, which states their reasons, over the classes
/// deriving <typeparamref name="TBase"/> and the shards giving each a <typeparamref name="TFixture"/>.
/// </summary>
public abstract class ShardRuleTests<TBase, TFixture>(IReadOnlyList<string> shards, int minimumClasses)
    where TFixture : class
{
    private const string Rules = "the shard rules on ApiIntegrationShards";

    private static readonly Type[] Types = typeof(TBase).Assembly.GetTypes();

    private static readonly Type[] Tests = [.. Types.Where(type => typeof(TBase).IsAssignableFrom(type))];

    [Fact]
    public void EveryTestClassNamesOneShardOnItself()
    {
        var offenders = Tests
            .Where(type => !type.IsAbstract)
            .Where(type => DeclaredCollections(type) is not [var name] || !shards.Contains(name))
            .Select(type => $"{type.FullName} [{string.Join(", ", DeclaredCollections(type))}]")
            .ToList();

        offenders.Should().BeEmpty("each class names one of {0} on itself, per {1}",
            string.Join(", ", shards), Rules);
    }

    [Fact]
    public void NoBaseClassNamesACollection()
    {
        var offenders = Tests
            .Where(type => type.IsAbstract && DeclaredCollections(type).Count > 0)
            .Select(type => type.FullName)
            .ToList();

        offenders.Should().BeEmpty("no base names a collection, per {0}", Rules);
    }

    [Fact]
    public void NoTestClassTakesTheFixtureAsAClassFixture()
    {
        var offenders = Tests
            .Where(type => type.GetInterfaces().Contains(typeof(IClassFixture<TFixture>)))
            .Select(type => type.FullName)
            .ToList();

        offenders.Should().BeEmpty("a class shares its shard's fixture rather than starting its own, per {0}", Rules);
    }

    [Fact]
    public void EveryShardIsACollectionWithAFixtureOfItsOwn()
    {
        var definitions = Types
            .Where(type => type.GetInterfaces().Contains(typeof(ICollectionFixture<TFixture>)))
            .Select(type => type.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(CollectionDefinitionAttribute))
                .Select(data => (string?)data.ConstructorArguments[0].Value)
                .SingleOrDefault())
            .ToList();

        definitions.Should().BeEquivalentTo(shards,
            "each shard needs one collection definition declaring the fixture, which is what gives " +
            "it a fixture of its own, and a definition outside the shards is a collection the " +
            "suite no longer runs in parallel");
    }

    [Fact]
    public void EveryShardHoldsTests()
    {
        var used = Tests.Where(type => !type.IsAbstract).SelectMany(DeclaredCollections).ToHashSet();

        shards.Should().OnlyContain(shard => used.Contains(shard),
            "an empty shard runs no test, so the others carry its share of the suite");
        Tests.Count(type => !type.IsAbstract).Should().BeGreaterThan(minimumClasses,
            "the suite has more test classes than that, and finding fewer means the scan lost them " +
            "rather than that the suite shrank");
    }

    private static List<string> DeclaredCollections(Type type) =>
        [.. type.GetCustomAttributesData()
            .Where(data => data.AttributeType == typeof(CollectionAttribute))
            .Select(data => (string)data.ConstructorArguments[0].Value!)];
}
