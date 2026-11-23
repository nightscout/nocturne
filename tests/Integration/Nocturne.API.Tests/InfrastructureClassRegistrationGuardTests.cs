using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Every descriptor the host's real startup holds for a class implemented in
/// <c>Nocturne.Infrastructure.Data</c>, in any namespace, is one Infrastructure's own registration
/// methods produce, once, with the same lifetime. A descriptor belongs to the class when its
/// service type, its implementation type or the type of its instance is declared there, so an
/// instance, a method group or a local that <c>InfrastructureRegistrationGuardTests</c> cannot see
/// in the source is still checked. That check reads only the descriptors, so it cannot tell a host
/// registration from the Infrastructure call it replaced.
/// </summary>
[Trait("Category", "Integration")]
public class InfrastructureClassRegistrationGuardTests(ServiceProviderValidationTests.MigratedDatabase fixture)
    : IClassFixture<ServiceProviderValidationTests.MigratedDatabase>
{
    private static readonly Assembly InfrastructureAssembly = typeof(NocturneDbContext).Assembly;

    private record Registration(Type ServiceType, Type? Implementation, ServiceLifetime Lifetime, object? Key);

    private static Registration Describe(ServiceDescriptor d) => new(
        d.ServiceType,
        d.IsKeyedService
            ? d.KeyedImplementationType ?? d.KeyedImplementationInstance?.GetType()
            : d.ImplementationType ?? d.ImplementationInstance?.GetType(),
        d.Lifetime,
        d.ServiceKey);

    private static bool IsImplementedByInfrastructure(Registration r) =>
        r.ServiceType.Assembly == InfrastructureAssembly || r.Implementation?.Assembly == InfrastructureAssembly;

    /// <summary>
    /// Each public registration method on its own collection, merged by the largest count it gives a
    /// registration: a method that runs another (<c>AddPostgreSqlInfrastructure</c> ends in
    /// <c>AddDataServices</c>) repeats that method's descriptors, which are still registered once.
    /// </summary>
    private static Dictionary<Registration, int> RegisteredByInfrastructure()
    {
        var config = PostgreSqlConfiguration.Resolve("Host=localhost;Database=guard", configuration: null);
        var expected = new Dictionary<Registration, int>();
        var methods = typeof(ServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(IServiceCollection)
                && m.GetParameters().All(p => p.ParameterType == typeof(IServiceCollection)
                    || p.ParameterType == typeof(PostgreSqlConfiguration)))
            .ToList();
        methods.Should().NotBeEmpty();

        foreach (var method in methods)
        {
            var services = new ServiceCollection();
            method.Invoke(null, [.. method.GetParameters().Select(p => p.ParameterType == typeof(IServiceCollection) ? (object)services : config)]);
            foreach (var group in services.Select(d => Describe(d)).GroupBy(r => r))
            {
                expected[group.Key] = Math.Max(expected.GetValueOrDefault(group.Key), group.Count());
            }
        }

        return expected;
    }

    [Fact]
    public async Task EveryInfrastructureClassTheHostRegistersIsOneInfrastructureRegistersOnceWithTheSameLifetime()
    {
        var registered = await RegisteredServices.CaptureAsync(fixture);
        var expected = RegisteredByInfrastructure();

        var classes = registered.Select(d => Describe(d)).Where(IsImplementedByInfrastructure).GroupBy(r => r).ToList();
        classes.Should().NotBeEmpty();

        classes
            .Where(g => !expected.TryGetValue(g.Key, out var count) || count != g.Count())
            .Select(g => $"{g.Key.ServiceType.Name} -> {g.Key.Implementation?.Name ?? "factory"} ({g.Key.Lifetime}) x{g.Count()}")
            .Should().BeEmpty("Infrastructure registers the classes it implements, once, with the same lifetime");
    }
}
