import { NotificationLabel } from "@nocturne/api-client";

// Some notifications are created by the backend with i18n keys for their title,
// subtitle, and action labels (the backend has no copy layer; per project
// convention strings live on the frontend). This maps those keys to human text.
// Any value not in the map is returned unchanged, so notifications that already
// carry human-readable text render as-is.
const LABEL_MAP: Record<NotificationLabel, string> = {
  [NotificationLabel.CompressionLowDetected]: "Compression low detected",
  [NotificationLabel.CompressionLowDetectedSubtitle]:
    "Possible overnight compression lows are ready to review",
  [NotificationLabel.Review]: "Review",
  [NotificationLabel.ReleaseNotes]: "Release notes",
  [NotificationLabel.UpdateAvailable]: "A Nocturne update is available",
  [NotificationLabel.InstanceKeyChanged]: "Enter some secrets again",
  [NotificationLabel.InstanceKeyChangedPlatform]: "Enter the platform credentials again",
  [NotificationLabel.OpenConnectors]: "Open connectors",
  [NotificationLabel.OpenAlerts]: "Open alerts",
  [NotificationLabel.AlertUnevaluableSubtitle]:
    "This rule cannot run as set, so it sends no alerts. Open the rule, check its settings, and save it.",
  [NotificationLabel.AlertPartlyUnevaluableSubtitle]:
    "This rule sends alerts, but its auto-resolve or smart snooze conditions cannot run as set. Open the rule, check its settings, and save it.",
  [NotificationLabel.AlertHeldSubtitle]:
    "This rule has waited 24 hours for usable data, so it cannot send alerts until usable data comes back. Open the rule to see the cause.",
  [NotificationLabel.AlertHeldClosedSubtitle]:
    "This alert had no usable data for 24 hours, so Nocturne closes the alert. The rule cannot send alerts until usable data comes back.",
  [NotificationLabel.AlertChannelStopped]: "An alert channel stopped",
  [NotificationLabel.AlertChannelStoppedSubtitle]: "An alert channel stopped.",
  [NotificationLabel.AlertChannelNoRecipient]: "An alert channel has no recipient",
  [NotificationLabel.AlertChannelNoRecipientSubtitle]: "An alert channel has no recipient.",
  [NotificationLabel.OpenPlatformSettings]: "Open platform settings",
  [NotificationLabel.ReleaseInstalled]: "Nocturne is updated",
  [NotificationLabel.TeamRenamed]: "A team has a new name",
  [NotificationLabel.TeamStaffJoined]: "A team has new staff",
  [NotificationLabel.TeamStaffWaiting]: "Team staff are waiting for your approval",
  [NotificationLabel.ShareLinkRotated]: "Your sharing link has been replaced",
  [NotificationLabel.ShareLinkRotatedSubtitle]:
    "The previous link no longer opens your data. Go to Public access in settings to create a new link, then send it to anyone who was using the old one.",
  // The release that first ran the share-token rotation filed these keys before they were renamed
  // to share_link_rotated. Keep them mapped for as long as those rows can exist, or the owners who
  // were notified see the raw key.
  [NotificationLabel.ShareLinkReset]: "Your sharing link has been replaced",
  [NotificationLabel.ShareLinkResetSubtitle]:
    "The previous link no longer opens your data. Go to Public access in settings to create a new link, then send it to anyone who was using the old one.",
  [NotificationLabel.DeviceClockDeviation]: "Device clock deviation corrected",
  [NotificationLabel.DeviceClockDeviationSubtitle]:
    "A connected account's device clock ran at a different offset from its timezone for a while. Affected timestamps are adjusted as that data syncs, so some historical entries may shift.",
  [NotificationLabel.DeviceClockAhead]: "A device clock is ahead",
  [NotificationLabel.DeviceClockAheadSubtitle]:
    "A device sent data with a time more than 5 minutes after the current time. Alerts do not use this data until the current time is near that time. Set the clock on the device to the correct time.",
  [NotificationLabel.DeclaredZoneChanged]: "Timezone updated from your connected account",
  [NotificationLabel.RejectedUploads]: "Some uploads could not be stored",
  [NotificationLabel.RejectedUploadsSubtitle]:
    "An uploader sent a record Nocturne could not store. Most uploaders treat it as saved, so it is kept under Data Quality for you to review or export.",
  [NotificationLabel.RawMmolProfileValues]: "Some profile values look like mmol/L",
  [NotificationLabel.RawMmolProfileValuesSubtitle]:
    "Some target or ISF values look like mmol/L values stored as mg/dL. These values change insulin and alert calculations. Go to your profile settings to check and correct them.",
  [NotificationLabel.Dismiss]: "Dismiss",
  [NotificationLabel.DeclaredZoneChangedSubtitle]:
    "Your connected account now reports a different timezone, so a matching entry was added to your timezone timeline. You can review or edit it in settings.",
};

const LABEL_TEXT: Readonly<Record<string, string>> = LABEL_MAP;

/** Resolve a notification title/subtitle/action label, mapping known i18n keys
 *  to human text and passing through anything already human-readable. */
export function resolveNotificationLabel(
  value: string | undefined | null,
): string {
  if (!value) return "";
  return Object.hasOwn(LABEL_TEXT, value) ? (LABEL_TEXT[value] ?? value) : value;
}
