using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Checks and builds an alert rule's delivery channels from a request, for every writer that takes
/// channels from a caller: the rule editor and the setup hub's starter rules.
/// </summary>
public sealed class AlertRuleChannelWriter(ISecretEncryptionService encryption)
{
    /// <summary>
    /// Fills in a DM channel's destination from the caller's linked identity on that channel's
    /// platform and rejects a channel list whose destinations cannot deliver: a
    /// <c>device_action</c> channel naming an unknown kind or capability, a channel type with no
    /// delivery path, a channel type that needs a destination and was given none, or a destination
    /// the platform adapter cannot address. Nothing downstream inspects a destination, so an
    /// unrejected one becomes a channel that stores fine and never delivers. Returns why the first
    /// offender is rejected, or null when all channels are valid. <paramref name="subjectId"/> is
    /// the caller, whose linked identities fill in a DM destination.
    /// </summary>
    public async Task<string?> ResolveAndValidateAsync(
        List<CreateAlertRuleChannelRequest>? channels, NocturneDbContext db, Guid? subjectId, CancellationToken ct)
    {
        if (channels is null)
        {
            return null;
        }

        foreach (var ch in channels)
        {
            if (RejectOversizedSecret(ch) is { } badSecret)
            {
                return badSecret;
            }

            if (ch.ChannelType == ChannelType.DeviceAction)
            {
                if (RejectInvalidDeviceActionChannel(ch) is { } badDevice)
                {
                    return badDevice;
                }

                continue;
            }

            if (ChannelDestinations.SupersededBy(ch.ChannelType) is { } replacements)
            {
                return $"A {WireName(ch.ChannelType)} channel has no delivery path. Use "
                    + $"{string.Join(" or ", replacements.Select(WireName))} instead.";
            }

            if (ChannelDestinations.ResolvesFromLinkedIdentity(ch.ChannelType)
                && string.IsNullOrWhiteSpace(ch.Destination))
            {
                var platform = ChannelDestinations.PlatformOf(ch.ChannelType)!;
                ch.Destination = await ResolveLinkedPlatformUserIdAsync(db, platform, subjectId, ct);
                if (ch.Destination is null)
                {
                    var name = char.ToUpperInvariant(platform[0]) + platform[1..];
                    return $"No linked {name} account for this user. Link {name} under "
                        + $"Connectors & Apps, or enter a {name} user ID as the destination.";
                }
            }

            if (ChannelDestinations.RequiresDestination(ch.ChannelType)
                && string.IsNullOrWhiteSpace(ch.Destination))
            {
                return $"A {WireName(ch.ChannelType)} channel requires a destination.";
            }

            if (!ChannelDestinations.IsWellFormed(ch.ChannelType, ch.Destination))
            {
                return $"A {WireName(ch.ChannelType)} channel's destination must be "
                    + $"{ChannelDestinations.DescribeDestination(ch.ChannelType)}; "
                    + $"got '{ch.Destination}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// The largest signing secret accepted, measured in UTF-8 bytes because that — not the
    /// character count — is what the stored ciphertext is sized from. Stating the bound keeps a
    /// secret of legal length in non-Latin script from being discovered as a 500 at the column.
    /// </summary>
    private const int SecretMaxBytes = 256;

    private static string? RejectOversizedSecret(CreateAlertRuleChannelRequest ch)
    {
        var secret = ch.Secret?.Trim();
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(secret);
        return bytes <= SecretMaxBytes
            ? null
            : $"A {WireName(ch.ChannelType)} channel's signing secret must be at most "
                + $"{SecretMaxBytes} bytes once UTF-8 encoded; got {bytes}.";
    }

    private static string? RejectInvalidDeviceActionChannel(CreateAlertRuleChannelRequest ch)
    {
        if (string.IsNullOrWhiteSpace(ch.Destination) || !DeviceKinds.IsValid(ch.Destination))
        {
            return $"A device_action channel's destination must be a device kind "
                + $"({string.Join(", ", DeviceKinds.All)}); got '{ch.Destination}'.";
        }

        var requested = DeviceCapabilities.ParseRequestedCapabilities(SerializeMetadata(ch.Metadata));
        foreach (var capability in requested)
        {
            if (!DeviceCapabilities.IsKnown(capability))
            {
                return $"Unknown device capability '{capability}'.";
            }

            if (!DeviceCapabilities.Registry[capability].Kinds.Contains(ch.Destination))
            {
                return $"Capability '{capability}' is not available on "
                    + $"device kind '{ch.Destination}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the platform user ID linked to the calling subject within this tenant, or null when
    /// the subject has no active link on that platform. Resolution happens here rather than at
    /// delivery because a dispatch runs from the background orchestrator, where there is no caller
    /// to attribute a DM to — and a rule carries no owner of its own.
    /// </summary>
    private static async Task<string?> ResolveLinkedPlatformUserIdAsync(
        NocturneDbContext db, string platform, Guid? subjectId, CancellationToken ct)
    {
        if (subjectId is null)
        {
            return null;
        }

        var tenantId = db.TenantId;
        return await db.ChatIdentityDirectory
            .Where(d => d.TenantId == tenantId
                        && d.NocturneUserId == subjectId.Value
                        && d.Platform == platform
                        && d.IsActive)
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.PlatformUserId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Serialised name of a channel type, so error text matches the request wire format.</summary>
    private static string WireName(ChannelType channelType) =>
        JsonSerializer.Serialize(channelType).Trim('"');

    public static readonly IReadOnlyDictionary<(ChannelType, string), string> NoRetainedSecrets =
        new Dictionary<(ChannelType, string), string>();

    public AlertRuleChannelEntity Build(
        CreateAlertRuleChannelRequest req, Guid ruleId, Guid tenantId, int sortOrder,
        IReadOnlyDictionary<(ChannelType, string), string> retainedSecrets) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        AlertRuleId = ruleId,
        ChannelType = req.ChannelType,
        Destination = req.Destination ?? string.Empty,
        DestinationLabel = req.DestinationLabel,
        Metadata = SerializeMetadata(req.Metadata),
        Secret = ResolveSecret(req, retainedSecrets),
        SortOrder = sortOrder,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// The stored ciphertext of every channel that carries a signing secret, keyed by the pair a
    /// caller can still name after a read: an update replaces the channel list wholesale and mints
    /// new ids, and the secret is never echoed back, so a channel arriving without one has to be
    /// matched to its predecessor by type and destination.
    /// </summary>
    /// <remarks>
    /// A pair held by more than one stored secret retains none of them: the key cannot tell which
    /// of the duplicates an incoming channel descends from, and a guess would sign one receiver's
    /// alerts with another's secret. Ciphertext is compared rather than plaintext, so two channels
    /// sharing a destination are ambiguous even when the secret behind them is the same — they are
    /// re-entered rather than silently mismatched.
    /// </remarks>
    public static IReadOnlyDictionary<(ChannelType, string), string> CollectRetainedSecrets(
        IEnumerable<AlertRuleChannelEntity> channels) => channels
            .Where(c => c.Secret is not null)
            .GroupBy(c => (c.ChannelType, c.Destination))
            .Select(g => (g.Key, Secrets: g.Select(c => c.Secret!).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Secrets.Length == 1)
            .ToDictionary(g => g.Key, g => g.Secrets[0]);

    /// <summary>
    /// Ciphertext for the channel's signing secret. A secret omitted from the request keeps the one
    /// stored against this channel type and destination (the editor cannot re-send what it was
    /// never shown); one that is empty once trimmed clears it.
    /// </summary>
    private string? ResolveSecret(
        CreateAlertRuleChannelRequest req,
        IReadOnlyDictionary<(ChannelType, string), string> retainedSecrets)
    {
        if (req.Secret is null)
        {
            return retainedSecrets.TryGetValue(
                (req.ChannelType, req.Destination ?? string.Empty), out var retained)
                ? retained
                : null;
        }

        return EncryptSecret(req.Secret);
    }

    /// <summary>
    /// Ciphertext for a caller-supplied secret, or null when it is blank. Surrounding whitespace is
    /// dropped rather than signed with: it does not survive a copy-paste round trip through the
    /// receiver's own configuration.
    /// </summary>
    public string? EncryptSecret(string? secret)
    {
        var trimmed = secret?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : encryption.Encrypt(trimmed);
    }

    public static string? SerializeMetadata(object? metadata) =>
        metadata is not null ? JsonSerializer.Serialize(metadata) : null;
}
