/**
 * Non-standard tag-to-property mappings that differ from simple camelCase.
 * Standard mappings (e.g., "Trackers" -> "trackers") are handled by the fallback.
 *
 * Only add entries here when the api-client.ts property name differs
 * from the simple camelCase of the cleaned tag name.
 * See: src/Web/packages/app/src/lib/api/api-client.ts
 */
const TAG_TO_PROPERTY_OVERRIDES: Record<string, string> = {
  'Foods': 'foodsV4',
  'DData': 'v2DData',
  'Loop': 'loopNotifications',
  'Notifications': 'v2Notifications',
  'Properties': 'v2Properties',
  'Summary': 'v2Summary',
  'Prediction': 'predictions',
};

/**
 * Get the apiClient property name for an OpenAPI tag.
 * Strips version prefixes (e.g., "V4 Trackers" -> "Trackers") and
 * maps to the corresponding api-client.ts property name.
 */
export function getClientPropertyName(tag: string): string {
  const cleaned = tag.replace(/^V\d+\s*/i, '');
  return TAG_TO_PROPERTY_OVERRIDES[cleaned] ?? cleaned.charAt(0).toLowerCase() + cleaned.slice(1);
}
