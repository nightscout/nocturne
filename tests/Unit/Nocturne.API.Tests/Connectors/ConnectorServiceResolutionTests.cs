using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Every connector service is built by its typed HttpClient factory, which <c>ValidateOnBuild</c>
/// does not look inside, so a constructor its installer cannot satisfy surfaces only at the first
/// sync. Each one is resolved here from the installers' registrations, with the host's own services
/// (publisher, tenant, persistence) stood in by mocks.
/// </summary>
public class ConnectorServiceResolutionTests
{
    public static TheoryData<Type> ConnectorServiceTypes()
    {
        var services = new ServiceCollection();
        services.AddConnectors(new ConfigurationBuilder().Build());

        return [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(IConnectorSyncExecutor))
            .Select(descriptor => descriptor.ImplementationType)
            .Select(ClosedExecutorOf)
            .OfType<Type>()
            .Select(type => type.GetGenericArguments()[0])];
    }

    private static Type? ClosedExecutorOf(Type? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ConnectorSyncExecutor<,>))
                return current;

        return null;
    }

    [Fact]
    public void EveryShippedConnectorIsCovered()
    {
        ConnectorServiceTypes().Count.Should().Be(14);
    }

    [Theory]
    [MemberData(nameof(ConnectorServiceTypes))]
    public void ConnectorService_ResolvesFromItsInstallerRegistrations(Type serviceType)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBaseConnectorServices();
        services.AddConnectors(new ConfigurationBuilder().Build());
        StandInForHostServices(services, serviceType, []);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(serviceType).Should().BeAssignableTo(serviceType);
    }

    /// <summary>
    /// Mocks every interface a constructor in <paramref name="type"/>'s graph requires that no
    /// installer registers. Concrete types are walked rather than mocked, so a class an installer
    /// forgot still fails to resolve.
    /// </summary>
    private static void StandInForHostServices(IServiceCollection services, Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return;

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        foreach (var parameter in constructor.GetParameters())
        {
            if (parameter.HasDefaultValue)
                continue;

            var required = parameter.ParameterType;
            var registered = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == required
                || (required.IsGenericType && descriptor.ServiceType == required.GetGenericTypeDefinition()));

            if (registered is not null)
            {
                if (registered.ImplementationType is { IsGenericTypeDefinition: false } implementation)
                    StandInForHostServices(services, implementation, visited);
                else if (!required.IsInterface && !required.IsAbstract)
                    StandInForHostServices(services, required, visited);
                continue;
            }

            if ((required.IsInterface || required.IsAbstract) && !IsConnectorOwned(required))
            {
                var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(required))!;
                services.AddSingleton(required, mock.Object);
            }
        }
    }

    /// <summary>
    ///     Registered by the connector runtime itself (<c>AddBaseConnectorServices</c>) and taken by
    ///     every connector service and token provider, so it is never stood in.
    /// </summary>
    private static bool IsConnectorOwned(Type type) => type == typeof(IRetryDelayStrategy);
}
