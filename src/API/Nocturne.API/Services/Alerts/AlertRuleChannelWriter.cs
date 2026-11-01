using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Checks an alert rule's delivery channels from a request and turns them into what the repository
/// stores, for every writer that takes channels from a caller: the rule editor and the setup hub's
/// starter rules.
/// </summary>
public sealed class AlertRuleChannelWriter(ISecretEncryptionService encryption)
{
    /// <summary>
    /// Fills in a DM channel's destination from the caller's linked identity on that channel's
    /// platform, routes an in-app channel (see <see cref="ResolveInAppRecipientAsync"/>), and
    /// rejects a channel list whose destinations cannot deliver: an in-app channel naming no
    /// member, a <c>device_action</c> channel naming an unknown kind or capability, a channel type
    /// with no delivery path, a channel type that needs a destination and was given none, or a
    /// destination the platform adapter cannot address. Nothing downstream inspects a destination,
    /// so an unrejected one becomes a channel that stores fine and never delivers. Returns why the
    /// first offender is rejected, or null when all channels are valid. <paramref name="subjectId"/>
    /// is the caller, whose linked identities fill in a DM destination.
    /// </summary>
    /// <param name="storedRecipients">
    /// The in-app recipients already stored where these channels come from, which may have left
    /// the site since.
    /// </param>
    public async Task<string?> ResolveAndValidateAsync(
        List<CreateAlertRuleChannelRequest>? channels,
        IReadOnlySet<string> storedRecipients,
        NocturneDbContext db,
        ITenantMembershipRepository members,
        Guid? subjectId,
        CancellationToken ct)
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

            if (ChannelDestinations.RoutesToMember(ch.ChannelType))
            {
                var goesToSaver = string.IsNullOrWhiteSpace(ch.Destination)
                                  || storedRecipients.Contains(ch.Destination);
                var member = await ResolveInAppRecipientAsync(members, db.TenantId, ch.Destination, storedRecipients, subjectId, ct);
                if (member is null)
                {
                    return goesToSaver
                        ? $"A {WireName(ch.ChannelType)} channel without a member to name is "
                            + "delivered to the member who saves it, and this caller is not a "
                            + "member of this site."
                        : $"A {WireName(ch.ChannelType)} channel's destination must be the subject "
                            + $"id of a member of this site; got '{ch.Destination}'.";
                }

                ch.Destination = member.Value.ToString();
                continue;
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

    /// <summary>
    /// The member an in-app channel delivers to: the member it names, or the caller when it names
    /// none. Null when it names a subject who may not receive (<see cref="InAppRecipients"/>).
    /// </summary>
    /// <remarks>
    /// A stored recipient who has since left the site, or can no longer receive, goes to the
    /// caller as well, rather than failing the save: the editor sends back the destination it
    /// loaded, so refusing it would leave the rule uneditable, and keeping it would leave the
    /// channel reaching no one.
    /// </remarks>
    private static async Task<Guid?> ResolveInAppRecipientAsync(
        ITenantMembershipRepository members, Guid tenantId, string? destination, IReadOnlySet<string> storedRecipients,
        Guid? subjectId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(destination)
            && await InAppRecipients.ResolveAsync(members, tenantId, destination, ct) is { } named)
        {
            return named;
        }

        if (!string.IsNullOrWhiteSpace(destination) && !storedRecipients.Contains(destination))
        {
            return null;
        }

        return await InAppRecipients.ResolveAsync(members, tenantId, subjectId?.ToString(), ct);
    }

    /// <summary>The in-app recipients stored on <paramref name="channels"/>.</summary>
    public static HashSet<string> InAppRecipientsOf(IEnumerable<AlertRuleChannelSnapshot> channels) =>
        channels.Where(c => c.ChannelType == ChannelType.InApp).Select(c => c.Destination).ToHashSet();

    public static readonly IReadOnlySet<string> NoStoredRecipients = new HashSet<string>();

    /// <summary>Serialised name of a channel type, so error text matches the request wire format.</summary>
    private static string WireName(ChannelType channelType) =>
        JsonSerializer.Serialize(channelType).Trim('"');

    /// <summary>
    /// The channel as stored. A secret omitted from the request keeps the one stored against this
    /// channel type and destination (<see cref="AlertRuleChannelWrite.KeepsStoredSecret"/>); one
    /// that is empty once trimmed clears it.
    /// </summary>
    public AlertRuleChannelWrite ToWrite(CreateAlertRuleChannelRequest req) => new(
        req.ChannelType,
        req.Destination ?? string.Empty,
        req.DestinationLabel,
        SerializeMetadata(req.Metadata),
        Secret: req.Secret is null ? null : EncryptSecret(req.Secret),
        KeepsStoredSecret: req.Secret is null);

    /// <inheritdoc cref="ToWrite(CreateAlertRuleChannelRequest)"/>
    public List<AlertRuleChannelWrite> ToWrites(IEnumerable<CreateAlertRuleChannelRequest> requests) =>
        requests.Select(ToWrite).ToList();

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
