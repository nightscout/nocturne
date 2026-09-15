using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
///     A token provider keys the session cache by its own <c>ConnectorName</c>, while everything that
///     looks that state up again — the background sync's health bookkeeping, invalidation on a
///     credential change — keys off the registration. The two names are written in different files,
///     nothing joins them, and a mismatch is silent: the write lands under a key no reader asks for.
/// </summary>
public class AuthTokenProviderCacheKeyTests
{
    [Fact]
    public void EveryTokenProvider_KeysTheCacheByItsConfigurationsRegisteredName()
    {
        var providers = TokenProviders().ToList();

        providers.Should().HaveCountGreaterThan(5, "the shipped connectors should have been scanned");

        providers.Should().OnlyContain(
            p => p.CacheKeyName == ConnectorRegistrationAttribute.DeclaredOn(p.ConfigType).ConnectorName,
            "a provider writing under a name nothing reads back is a cache no reader can find");
    }

    private sealed record Provider(Type ProviderType, Type ConfigType, string CacheKeyName);

    /// <summary>
    ///     Reads each provider's <c>ConnectorName</c> without constructing it, because every
    ///     implementation is a constant and construction would need the whole connector's dependency
    ///     graph.
    /// </summary>
    private static IEnumerable<Provider> TokenProviders()
    {
        foreach (var type in ConnectorInstallers.Types())
        {
            if (type.IsAbstract) continue;

            var closed = ConnectorInstallers.ClosedBaseOf(type, typeof(AuthTokenProviderBase<>));
            if (closed == null) continue;

            var name = (string)type
                .GetProperty("ConnectorName", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(RuntimeHelpers.GetUninitializedObject(type))!;

            yield return new Provider(type, closed.GetGenericArguments()[0], name);
        }
    }
}
