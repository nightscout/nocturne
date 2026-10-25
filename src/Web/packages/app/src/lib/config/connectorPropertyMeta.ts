/**
 * Frontend translation dictionary for connector property keys.
 * Maps backend ConnectorPropertyKey enum values to localized labels, descriptions, and categories.
 */

export type PropertyCategory = 'General' | 'Credentials' | 'Sync' | 'Advanced';

export type PropertyMeta = {
  label: string;
  description: string;
  category: PropertyCategory;
};

/**
 * One entry per backend ConnectorPropertyKey; ConnectorPropertyMetaMirrorTests reads this file and
 * that enum and fails if the two sets differ.
 */
export const connectorPropertyMeta = {
  // Base configuration
  TimezoneOffset: {
    label: 'Timezone offset',
    description: 'Hours offset from UTC for timestamp adjustments',
    category: 'General',
  },
  Enabled: {
    label: 'Enabled',
    description: 'Whether this connector is active and syncing data',
    category: 'General',
  },
  MaxRetryAttempts: {
    label: 'Max attempts',
    description: 'Total connection attempts per sync before giving up (minimum 1)',
    category: 'Advanced',
  },
  BatchSize: {
    label: 'Batch size',
    description: 'Number of records to process per batch',
    category: 'Advanced',
  },
  SyncIntervalMinutes: {
    label: 'Sync interval',
    description: 'How often to sync data from the source (in minutes)',
    category: 'Sync',
  },

  // Sync toggles
  SyncGlucose: {
    label: 'Sync glucose',
    description: 'Sync continuous glucose monitor (CGM) readings',
    category: 'Sync',
  },
  SyncManualBG: {
    label: 'Sync manual BG',
    description: 'Sync manual blood glucose meter readings',
    category: 'Sync',
  },
  SyncBoluses: {
    label: 'Sync boluses',
    description: 'Sync insulin bolus delivery records',
    category: 'Sync',
  },
  SyncBasalInjections: {
    label: 'Sync basal injections',
    description: 'Sync long-acting basal insulin injections',
    category: 'Sync',
  },
  SyncCarbIntake: {
    label: 'Sync carb intake',
    description: 'Sync carbohydrate intake entries',
    category: 'Sync',
  },
  SyncBolusCalculations: {
    label: 'Sync bolus calculations',
    description: 'Sync bolus calculator recommendations and inputs',
    category: 'Sync',
  },
  SyncNotes: {
    label: 'Sync notes',
    description: 'Sync user notes and annotations',
    category: 'Sync',
  },
  SyncDeviceEvents: {
    label: 'Sync device events',
    description: 'Sync device-specific events such as prime, rewind, and calibration',
    category: 'Sync',
  },
  SyncStateSpans: {
    label: 'Sync state spans',
    description:
      'Sync device state periods like suspend, resume, and mode changes. Turning this off also declines the active-profile and basal-program periods, so the therapy timeline and chart bands lose them.',
    category: 'Sync',
  },
  SyncTempBasals: {
    label: 'Sync temp basals',
    description: 'Sync temporary basal rate adjustments',
    category: 'Sync',
  },
  SyncProfiles: {
    label: 'Sync profiles',
    description: 'Sync basal rate profiles and settings',
    category: 'Sync',
  },
  SyncDeviceStatus: {
    label: 'Sync device status',
    description: 'Sync device status updates including battery and reservoir levels',
    category: 'Sync',
  },
  SyncActivity: {
    label: 'Sync activity',
    description: 'Sync activity and exercise data',
    category: 'Sync',
  },
  SyncFood: {
    label: 'Sync food',
    description: 'Sync food database entries and meal records',
    category: 'Sync',
  },
  SyncSteps: {
    label: 'Sync steps',
    description: 'Sync step-count measurements',
    category: 'Sync',
  },
  SyncHeartRate: {
    label: 'Sync heart rate',
    description: 'Sync heart-rate measurements',
    category: 'Sync',
  },
  SyncBodyWeight: {
    label: 'Sync body weight',
    description: 'Sync body-weight measurements',
    category: 'Sync',
  },
  SyncSleep: {
    label: 'Sync sleep',
    description: 'Sync sleep sessions',
    category: 'Sync',
  },

  // Common credentials
  Username: {
    label: 'Username',
    description: 'Account username for authentication',
    category: 'Credentials',
  },
  Password: {
    label: 'Password',
    description: 'Account password for authentication',
    category: 'Credentials',
  },
  Email: {
    label: 'Email',
    description: 'Email address for account login',
    category: 'Credentials',
  },
  ClientId: {
    label: 'Client ID',
    description: 'OAuth client identifier issued by the provider',
    category: 'Credentials',
  },
  ImportFrom: {
    label: 'Import from',
    description: 'Earliest date to retrieve from the provider',
    category: 'Sync',
  },
  PreviewOnly: {
    label: 'Preview only',
    description: 'Inspect available data before enabling imports',
    category: 'Advanced',
  },
  GrantedScopes: {
    label: 'Granted scopes',
    description: 'OAuth permissions granted by the provider',
    category: 'Credentials',
  },

  // Common server/region
  Server: {
    label: 'Server',
    description: 'Server region (US, EU, or other regional endpoint)',
    category: 'General',
  },
  Region: {
    label: 'Region',
    description: 'Regional server endpoint',
    category: 'General',
  },

  // Common connection
  PatientId: {
    label: 'Patient ID',
    description: 'Patient identifier for follower or caregiver accounts',
    category: 'Credentials',
  },
  UserId: {
    label: 'User ID',
    description: 'User identifier for the account',
    category: 'Credentials',
  },
  PatientUsername: {
    label: 'Patient username',
    description: 'Username of the patient to follow when the account follows more than one',
    category: 'Credentials',
  },
  RefreshToken: {
    label: 'Refresh token',
    description: 'Long-lived token used in place of the password once issued',
    category: 'Credentials',
  },

  // Nightscout-specific
  Url: {
    label: 'URL',
    description: 'Site URL (e.g., https://yoursite.herokuapp.com)',
    category: 'General',
  },
  RealtimeUrl: {
    label: 'Realtime URL',
    description: 'Optional Nightscout origin used for live Socket.IO updates when the data URL is an adapter',
    category: 'Advanced',
  },
  ApiSecret: {
    label: 'API secret',
    description: 'Nightscout API_SECRET for authentication',
    category: 'Credentials',
  },
  MaxCount: {
    label: 'Max count',
    description: 'Maximum number of records to fetch per request',
    category: 'Advanced',
  },

  // Glooko-specific
  UseV3Api: {
    label: 'Use V3 API',
    description: 'Use the newer Glooko V3 API for data retrieval',
    category: 'Advanced',
  },
  UseSsv2Sync: {
    label: 'Use Granular Sync (Experimental)',
    description:
      'Sync each kind of data from its own Glooko feed, resuming where the last sync stopped instead of re-reading a date window. Imports pen injections, app-logged doses, extended boluses and more. Replaces the V3 API setting when on.',
    category: 'Advanced',
  },
  V3IncludeCgmBackfill: {
    label: 'Include CGM backfill',
    description: 'Include historical CGM data when using V3 API',
    category: 'Advanced',
  },
  AutoClockCorrection: {
    label: 'Automatic clock correction',
    description:
      'Let device-clock evidence from the account correct imported timestamps and keep the timezone timeline current',
    category: 'Advanced',
  },

  // MyLife-specific
  ServiceUrl: {
    label: 'Service URL',
    description: 'MyLife service endpoint URL',
    category: 'Advanced',
  },
  EnableMealCarbConsolidation: {
    label: 'Consolidate meal carbs',
    description: 'Combine multiple carb entries from the same meal',
    category: 'Advanced',
  },
  EnableTempBasalConsolidation: {
    label: 'Consolidate temp basals',
    description: 'Combine consecutive temporary basal segments',
    category: 'Advanced',
  },
  TempBasalConsolidationWindowMinutes: {
    label: 'Temp basal window',
    description: 'Time window in minutes for consolidating temp basals',
    category: 'Advanced',
  },
  AppPlatform: {
    label: 'App platform',
    description: 'Mobile platform identifier (iOS/Android)',
    category: 'Advanced',
  },
  AppVersion: {
    label: 'App version',
    description: 'Mobile app version string',
    category: 'Advanced',
  },

  // MyFitnessPal and Glooko
  LookbackDays: {
    label: 'Lookback days',
    description: 'How many days back each sync reaches',
    category: 'Sync',
  },
  LastFullWalkAt: {
    label: 'Last full walk',
    description: 'When the diary was last read back to its first entry',
    category: 'Advanced',
  },

  // CareLink-specific
  CountryCode: {
    label: 'Country code',
    description: 'Two-letter country code of the CareLink account',
    category: 'General',
  },
  LanguageCode: {
    label: 'Language code',
    description: 'Two-letter language code of the CareLink account',
    category: 'General',
  },

  // Tandem-specific
  PumpSerialNumber: {
    label: 'Pump serial number',
    description: 'Serial number of the pump to follow when the account has more than one',
    category: 'General',
  },
  FetchAllEventTypes: {
    label: 'Fetch all event types',
    description: 'Read every pump history event type rather than the default filtered set',
    category: 'Advanced',
  },
  IgnoreZeroUnitBasal: {
    label: 'Ignore zero-unit basal',
    description: 'Skip basal entries that resolve to a near-zero rate',
    category: 'Advanced',
  },
  // OAuth and Webhooks
  AccessToken: {
    label: 'Access token',
    description: 'OAuth access token for the service',
    category: 'Credentials',
  },
  WebhookEnabled: {
    label: 'Webhook enabled',
    description: 'Enable real-time updates via webhooks',
    category: 'Sync',
  },
  WebhookSecret: {
    label: 'Webhook secret',
    description: 'Secret key for validating webhook requests',
    category: 'Credentials',
  },
  // Status thresholds
  ActiveThresholdMinutes: {
    label: 'Active threshold (minutes)',
    description: 'Minutes without new data before status changes from active to stale',
    category: 'Advanced',
  },
  StaleThresholdMinutes: {
    label: 'Stale threshold (minutes)',
    description: 'Minutes without new data before status changes from stale to inactive',
    category: 'Advanced',
  },
  // Write-back
  WriteBackEnabled: {
    label: 'Enable write-back',
    description: 'Allow writing data back to the source service',
    category: 'Advanced',
  },
  WriteBackBatchSize: {
    label: 'Write-back batch size',
    description: 'Number of records to write back per batch',
    category: 'Advanced',
  },
  GlucoseProcessing: {
    label: 'Glucose processing',
    description: 'How the connector labels its glucose readings (smoothed or unsmoothed)',
    category: 'General',
  },
} satisfies Record<string, PropertyMeta>;

/** String key names, taken from the entries above. */
export type ConnectorPropertyKeyName = keyof typeof connectorPropertyMeta;

const PROPERTY_META_BY_KEY: ReadonlyMap<string, PropertyMeta> = new Map<string, PropertyMeta>(
  Object.entries(connectorPropertyMeta)
);

/**
 * Convert PascalCase/camelCase to Title Case with spaces.
 * Used as fallback for unknown property keys.
 */
export function formatPropertyName(name: string): string {
  return name
    .replace(/([A-Z])/g, ' $1')
    .replace(/^./, (s) => s.toUpperCase())
    .trim();
}

/**
 * Get metadata for a property key with fallback for unknown keys.
 * Handles both PascalCase (enum names) and camelCase (schema property keys).
 * @param key The property key to look up
 * @returns PropertyMeta with label, description, and category
 */
export function getPropertyMeta(key: string): PropertyMeta {
  // Direct match (PascalCase from enum)
  const direct = PROPERTY_META_BY_KEY.get(key);
  if (direct) {
    return direct;
  }

  // Convert camelCase to PascalCase for lookup (schema keys are camelCased)
  const pascalKey = key.charAt(0).toUpperCase() + key.slice(1);
  const pascal = PROPERTY_META_BY_KEY.get(pascalKey);
  if (pascal) {
    return pascal;
  }

  return {
    label: formatPropertyName(key),
    description: '',
    category: 'General',
  };
}
