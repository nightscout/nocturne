/**
 * Get the apiClient property name for an OpenAPI tag using camelCase fallback.
 * Strips version prefixes (e.g., "V4 Trackers" -> "Trackers") and converts to camelCase.
 * Used as fallback when no [ClientPropertyName] attribute is set on the controller.
 */
export function getClientPropertyName(tag: string): string {
  const cleaned = tag.replace(/^V\d+\s*/i, '').replace(/\s+/g, '');
  return cleaned.charAt(0).toLowerCase() + cleaned.slice(1);
}
