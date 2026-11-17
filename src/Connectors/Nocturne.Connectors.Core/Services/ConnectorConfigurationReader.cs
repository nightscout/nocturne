using System.Reflection;
using System.Text.Json;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Reads a tenant's stored connector configuration and secrets over a starting configuration,
///     without writing anything. Every per-tenant configuration starts from the registration
///     defaults, so <c>CONNECT_{NAME}_*</c> settings reach each path that signs in.
/// </summary>
/// <remarks>
///     Reads throw <see cref="InvalidOperationException"/> or <see cref="JsonException"/> when a stored
///     row cannot be read or a secret cannot be decrypted.
/// </remarks>
internal sealed class ConnectorConfigurationReader<TConfig>(
    IConnectorRegistration<TConfig> registration,
    IConnectorConfigurationService configService)
    where TConfig : BaseConnectorConfiguration, new()
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     The registration defaults with every secret property reset to its unset value.
    /// </summary>
    public TConfig DefaultsWithoutSecrets
    {
        get
        {
            var config = Seed(registration.Defaults, null);
            var unset = new TConfig();
            foreach (var property in typeof(TConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanWrite && p.GetCustomAttribute<ConnectorPropertyAttribute>()?.Secret == true))
                property.SetValue(config, property.GetValue(unset));
            return config;
        }
    }

    /// <summary>
    ///     The tenant's stored configuration bound over <paramref name="source"/>, or null when the
    ///     tenant has no configuration row. A row without configuration is returned disabled.
    /// </summary>
    public async Task<TConfig?> ReadConfigurationAsync(TConfig source, CancellationToken ct)
    {
        var row = await configService.GetConfigurationAsync(registration.ConnectorName, ct);
        if (row is null)
            return null;

        var config = Seed(source, row.Configuration);
        if (row.Configuration is null)
            config.Enabled = false;
        return config;
    }

    /// <summary>
    ///     Binds the tenant's stored secrets over <paramref name="config"/>, only those keyed in
    ///     <paramref name="only"/> when it is given.
    /// </summary>
    public async Task ApplySecretsAsync(TConfig config, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        var secrets = await configService.GetSecretsAsync(registration.ConnectorName, ct);
        if (only != null)
            secrets = secrets.Where(s => only.Contains(s.Key)).ToDictionary();
        if (secrets.Count > 0)
            ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);
    }

    /// <summary>
    ///     A fresh copy of <paramref name="source"/> with <paramref name="configuration"/> bound over it.
    /// </summary>
    public static TConfig Seed(TConfig source, JsonDocument? configuration)
    {
        var config = JsonSerializer.Deserialize<TConfig>(Serialize(source), CloneOptions)!;
        if (configuration != null)
            ConnectorConfigurationBinder.ApplyJsonToConfig(configuration, config);
        return config;
    }

    /// <summary>
    ///     Whether binding <paramref name="configuration"/> over <paramref name="config"/> would
    ///     change any of its values.
    /// </summary>
    public static bool WouldChange(TConfig config, JsonDocument configuration) =>
        Serialize(Seed(config, configuration)) != Serialize(config);

    private static string Serialize(TConfig config) => JsonSerializer.Serialize(config, CloneOptions);
}
