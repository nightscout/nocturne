// Some notifications are created by the backend with i18n keys for their title,
// subtitle, and action labels (the backend has no copy layer; per project
// convention strings live on the frontend). This maps those keys to human text.
// Any value not in the map is returned unchanged, so notifications that already
// carry human-readable text render as-is.
const LABEL_MAP: Record<string, string> = {
  compression_low_detected: "Compression low detected",
  compression_low_detected_subtitle:
    "Possible overnight compression lows are ready to review",
  review: "Review",
  share_link_rotated: "Your sharing link has been replaced",
  share_link_rotated_subtitle:
    "The previous link no longer opens your data. Go to Public access in settings to create a new link, then send it to anyone who was using the old one.",
  // The release that first ran the share-token rotation filed these keys before they were renamed
  // to share_link_rotated. Keep them mapped for as long as those rows can exist, or the owners who
  // were notified see the raw key.
  share_link_reset: "Your sharing link has been replaced",
  share_link_reset_subtitle:
    "The previous link no longer opens your data. Go to Public access in settings to create a new link, then send it to anyone who was using the old one.",
};

/** Resolve a notification title/subtitle/action label, mapping known i18n keys
 *  to human text and passing through anything already human-readable. */
export function resolveNotificationLabel(
  value: string | undefined | null,
): string {
  if (!value) return "";
  return LABEL_MAP[value] ?? value;
}
