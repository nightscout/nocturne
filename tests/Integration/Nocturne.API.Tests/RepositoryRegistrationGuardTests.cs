using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A repository implemented in <c>Nocturne.Infrastructure.Data</c> is registered by that project's
/// own registration methods, never by the API host, so a host that wants one calls Infrastructure
/// and the lifetime lives beside the class.
/// </summary>
[Trait("Category", "Integration")]
public class RepositoryRegistrationGuardTests(ServiceProviderValidationTests.MigratedDatabase fixture)
    : IClassFixture<ServiceProviderValidationTests.MigratedDatabase>
{
    private static readonly Dictionary<Type, string> RegisteredTwice = new()
    {
        [typeof(Nocturne.Infrastructure.Data.Abstractions.IDiscrepancyAnalysisRepository)] =
            "the compatibility proxy registers it again with the same implementation and lifetime",
    };

    private static readonly string RepositoryNamespace = typeof(AlertRepository).Namespace!;

    private static bool IsInfrastructureRepository(Type? type) =>
        type is not null
        && type.Assembly == typeof(AlertRepository).Assembly
        && type.Namespace is { } ns
        && (ns == RepositoryNamespace || ns.StartsWith(RepositoryNamespace + ".", StringComparison.Ordinal));

    private static List<ServiceDescriptor> RegisteredByInfrastructure()
    {
        var services = new ServiceCollection();
        var registrations = typeof(ServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(IServiceCollection)
                && m.GetParameters() is [{ ParameterType: var only }]
                && only == typeof(IServiceCollection))
            .ToList();
        registrations.Should().NotBeEmpty();
        foreach (var registration in registrations)
        {
            registration.Invoke(null, [services]);
        }

        return [.. services];
    }

    [Fact]
    public async Task EveryInfrastructureRepositoryIsRegisteredByInfrastructureAndOnlyThere()
    {
        var registered = await RegisteredServices.CaptureAsync(fixture);
        var infrastructure = RegisteredByInfrastructure()
            .GroupBy(d => (d.ServiceType, d.ImplementationType, d.Lifetime))
            .ToDictionary(g => g.Key, g => g.Count());

        var repositories = registered
            .Where(d => !d.IsKeyedService && IsInfrastructureRepository(d.ImplementationType))
            .GroupBy(d => (d.ServiceType, d.ImplementationType, d.Lifetime))
            .ToList();
        repositories.Should().NotBeEmpty();

        repositories
            .Where(g => !RegisteredTwice.ContainsKey(g.Key.ServiceType))
            .Where(g => !infrastructure.TryGetValue(g.Key, out var count) || count != g.Count())
            .Select(g => $"{g.Key.ServiceType.Name} -> {g.Key.ImplementationType!.Name} ({g.Key.Lifetime}) x{g.Count()}")
            .Should().BeEmpty("Infrastructure registers the repositories it implements, once, with the same lifetime");
    }
}
