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
};

/** Resolve a notification title/subtitle/action label, mapping known i18n keys
 *  to human text and passing through anything already human-readable. */
export function resolveNotificationLabel(
  value: string | undefined | null,
): string {
  if (!value) return "";
  return LABEL_MAP[value] ?? value;
}
