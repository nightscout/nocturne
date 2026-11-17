using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// The shards of the API integration suite. Each is an xUnit collection with an
/// <see cref="ApiIntegrationTestFixture"/> of its own (its own API host and database), so the
/// shards run in parallel while the tests inside one run one after another, cleaned between tests by
/// <see cref="ApiIntegrationTestBase"/>.
/// </summary>
/// <remarks>
/// Every concrete <see cref="ApiIntegrationTestBase"/> names its shard with
/// <c>[Collection(ApiIntegrationShards.…)]</c> on the class itself, and no base names one: a class
/// inherits its base's <c>[Collection]</c> unless it names its own, so a base's would put every new
/// class in a shard nobody chose. A class that names none has no fixture and fails to run. Nor may a
/// class take <see cref="ApiIntegrationTestFixture"/> as a class fixture, which would boot a private
/// host for it. <see cref="ApiIntegrationShardTests"/> holds these rules. The shards are balanced by
/// measured duration and a new class goes in the shortest; <c>tests/README.md</c> says how to measure
/// them.
/// </remarks>
public static class ApiIntegrationShards
{
    public const string One = "ApiIntegration1";
    public const string Two = "ApiIntegration2";
    public const string Three = "ApiIntegration3";
    public const string Four = "ApiIntegration4";

    public static readonly IReadOnlyList<string> All = [One, Two, Three, Four];
}

[CollectionDefinition(ApiIntegrationShards.One)]
public class ApiIntegrationShard1 : ICollectionFixture<ApiIntegrationTestFixture>;

[CollectionDefinition(ApiIntegrationShards.Two)]
public class ApiIntegrationShard2 : ICollectionFixture<ApiIntegrationTestFixture>;

[CollectionDefinition(ApiIntegrationShards.Three)]
public class ApiIntegrationShard3 : ICollectionFixture<ApiIntegrationTestFixture>;

[CollectionDefinition(ApiIntegrationShards.Four)]
public class ApiIntegrationShard4 : ICollectionFixture<ApiIntegrationTestFixture>;
