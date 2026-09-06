using Microsoft.Extensions.Configuration;
using Nocturne.Infrastructure.Data.Configuration;

namespace Nocturne.Infrastructure.Data.Tests.Configuration;

/// <summary>
/// Builds the configuration inputs the PostgreSQL resolution and registration take: a connection
/// string standing in for the one the host resolved, and a configuration carrying nothing but the
/// <see cref="PostgreSqlConfiguration.SectionName"/> section.
/// </summary>
internal static class PostgreSqlSection
{
    internal const string AppConnectionString =
        "Host=localhost;Database=nocturne;Username=nocturne_app;Password=pw";

    internal static IConfiguration With(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                s => $"{PostgreSqlConfiguration.SectionName}:{s.Key}",
                s => (string?)s.Value))
            .Build();
}
