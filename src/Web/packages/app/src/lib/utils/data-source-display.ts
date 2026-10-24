/**
 * Maps data source identifiers to human-readable display names.
 * Source identifiers come from DataSources constants on the backend.
 */

const DATA_SOURCE_DISPLAY_NAMES: Record<string, string> = {
  // CGM Connectors
  "dexcom-connector": "Dexcom",
  "libre-connector": "FreeStyle Libre",
  "minimed-connector": "Medtronic",
  "glooko-connector": "Glooko",
  "nightscout-connector": "Nightscout",
  "tidepool-connector": "Tidepool",
  "tconnectsync-connector": "t:connect",
  "mylife-connector": "mylife",

  // Mobile apps / uploaders
  xdrip: "xDrip+",
  spike: "Spike",

  // AID systems
  loop: "Loop",
  openaps: "OpenAPS",
  aaps: "AndroidAPS",
  iaps: "iAPS",
  trio: "Trio",

  // Manual entry
  manual: "Manual entry",
  careportal: "Careportal",
  "api-client": "API client",

  // Food
  "myfitnesspal-connector": "MyFitnessPal",

  // Import / migration
  "mongodb-import": "MongoDB import",
  "csv-import": "CSV import",
  "tidepool-import": "Tidepool import",

  // System
  "demo-service": "Demo",
  system: "System",
  websocket: "WebSocket",
};

/**
 * Get a human-readable display name for a data source identifier.
 * Returns null if the source is null/undefined.
 */
export function getDataSourceDisplayName(
  source: string | null | undefined
): string | null {
  if (!source) return null;

  const lower = source.toLowerCase();
  if (lower in DATA_SOURCE_DISPLAY_NAMES) {
    return DATA_SOURCE_DISPLAY_NAMES[lower];
  }

  // Fallback: title-case the raw string, replacing hyphens with spaces
  return source
    .replace(/-/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase());
}
