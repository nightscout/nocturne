using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform.InstanceKey;

public sealed record UndecryptableConnector(string ConnectorName, string DisplayName);

/// <param name="Destination">
/// The channel's label, or the hosts it posts to when it has none. Empty when it has neither.
/// </param>
public sealed record UndecryptableAlertChannel(Guid RuleId, string RuleName, string Destination);

public sealed record UndecryptablePlatformSetting(string Category, bool Enabled);

public sealed record TenantUndecryptableSecrets(
    IReadOnlyList<UndecryptableConnector> Connectors,
    IReadOnlyList<UndecryptableAlertChannel> AlertChannels)
{
    public bool IsEmpty => Connectors.Count == 0 && AlertChannels.Count == 0;
}

/// <summary>
/// Finds the stored secrets the current instance key cannot decrypt. The share-link token is not
/// one of them: the link is checked by its hash, and the settings page already says when its stored
/// copy cannot be shown.
/// </summary>
public class UndecryptableSecretScanner(
    INocturneDbContextFactory contexts,
    IConnectorConfigurationRepository connectorConfigurations,
    IAlertRuleRepository alertRules,
    ISecretEncryptionService encryption)
{
    /// <summary>
    /// The connector configurations and alert channels of the scope's tenant whose secrets do not
    /// decrypt.
    /// </summary>
    public async Task<TenantUndecryptableSecrets> ScanTenantAsync(CancellationToken cancellationToken)
    {
        var connectors = (await connectorConfigurations.ListAsync(cancellationToken))
            .Where(c => c.SecretsJson != "{}")
            .ToList();

        var channels = await alertRules.GetChannelSecretsAsync(cancellationToken);

        return new TenantUndecryptableSecrets(
            connectors
                .Where(c => !AllDecrypt(c.SecretsJson))
                .Select(c => new UndecryptableConnector(
                    c.ConnectorName,
                    ConnectorMetadataService.GetByConnectorId(c.ConnectorName)?.DisplayName ?? c.ConnectorName))
                .ToList(),
            channels
                .Where(c => !encryption.CanDecrypt(c.Secret))
                .Select(c => new UndecryptableAlertChannel(
                    c.AlertRuleId, c.RuleName, c.DestinationLabel ?? Hosts(c.Destination)))
                .ToList());
    }

    /// <summary>The platform settings categories whose credentials do not decrypt.</summary>
    public async Task<IReadOnlyList<UndecryptablePlatformSetting>> ScanPlatformAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        var settings = await db.PlatformSettings.AsNoTracking()
            .Where(s => s.EncryptedJson != "{}")
            .OrderBy(s => s.Category)
            .Select(s => new { s.Category, s.Enabled, s.EncryptedJson })
            .ToListAsync(cancellationToken);

        return settings
            .Where(s => !AllDecrypt(s.EncryptedJson))
            .Select(s => new UndecryptablePlatformSetting(s.Category, s.Enabled))
            .ToList();
    }

    private bool AllDecrypt(string secretsJson) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(secretsJson) ?? [])
            .Values.All(encryption.CanDecrypt);

    /// <summary>
    /// The hosts only: a webhook URL can carry a token in its path. A destination that is not an
    /// absolute URI is left out rather than shown.
    /// </summary>
    private static string Hosts(string destination) => string.Join(", ", destination
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "")
        .Where(host => host.Length > 0));
}
