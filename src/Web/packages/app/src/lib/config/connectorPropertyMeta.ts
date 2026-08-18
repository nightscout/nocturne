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
    label: 'Timezone Offset',
    description: 'Hours offset from UTC for timestamp adjustments',
    category: 'General',
  },
  Enabled: {
    label: 'Enabled',
    description: 'Whether this connector is active and syncing data',
    category: 'General',
  },
  MaxRetryAttempts: {
    label: 'Max Retry Attempts',
    description: 'Maximum number of retry attempts on failure',
    category: 'Advanced',
  },
  BatchSize: {
    label: 'Batch Size',
    description: 'Number of records to process per batch',
    category: 'Advanced',
  },
  SyncIntervalMinutes: {
    label: 'Sync Interval',
    description: 'How often to sync data from the source (in minutes)',
    category: 'Sync',
  },

  // Sync toggles
  SyncGlucose: {
    label: 'Sync Glucose',
    description: 'Sync continuous glucose monitor (CGM) readings',
    category: 'Sync',
  },
  SyncManualBG: {
    label: 'Sync Manual BG',
    description: 'Sync manual blood glucose meter readings',
    category: 'Sync',
  },
  SyncBoluses: {
    label: 'Sync Boluses',
    description: 'Sync insulin bolus delivery records',
    category: 'Sync',
  },
  SyncCarbIntake: {
    label: 'Sync Carb Intake',
    description: 'Sync carbohydrate intake entries',
    category: 'Sync',
  },
  SyncBolusCalculations: {
    label: 'Sync Bolus Calculations',
    description: 'Sync bolus calculator recommendations and inputs',
    category: 'Sync',
  },
  SyncNotes: {
    label: 'Sync Notes',
    description: 'Sync user notes and annotations',
    category: 'Sync',
  },
  SyncDeviceEvents: {
    label: 'Sync Device Events',
    description: 'Sync device-specific events such as prime, rewind, and calibration',
    category: 'Sync',
  },
  SyncStateSpans: {
    label: 'Sync State Spans',
    description: 'Sync device state periods like suspend, resume, and mode changes',
    category: 'Sync',
  },
  SyncTempBasals: {
    label: 'Sync Temp Basals',
    description: 'Sync temporary basal rate adjustments',
    category: 'Sync',
  },
  SyncProfiles: {
    label: 'Sync Profiles',
    description: 'Sync basal rate profiles and settings',
    category: 'Sync',
  },
  SyncDeviceStatus: {
    label: 'Sync Device Status',
    description: 'Sync device status updates including battery and reservoir levels',
    category: 'Sync',
  },
  SyncActivity: {
    label: 'Sync Activity',
    description: 'Sync activity and exercise data',
    category: 'Sync',
  },
  SyncFood: {
    label: 'Sync Food',
    description: 'Sync food database entries and meal records',
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
    label: 'Patient Username',
    description: 'Username of the patient to follow when the account follows more than one',
    category: 'Credentials',
  },
  RefreshToken: {
    label: 'Refresh Token',
    description: 'Long-lived token used in place of the password once issued',
    category: 'Credentials',
  },

  // Nightscout-specific
  Url: {
    label: 'URL',
    description: 'Site URL (e.g., https://yoursite.herokuapp.com)',
    category: 'General',
  },
  ApiSecret: {
    label: 'API Secret',
    description: 'Nightscout API_SECRET for authentication',
    category: 'Credentials',
  },
  MaxCount: {
    label: 'Max Count',
    description: 'Maximum number of records to fetch per request',
    category: 'Advanced',
  },

  // Glooko-specific
  UseV3Api: {
    label: 'Use V3 API',
    description: 'Use the newer Glooko V3 API for data retrieval',
    category: 'Advanced',
  },
  V3IncludeCgmBackfill: {
    label: 'Include CGM Backfill',
    description: 'Include historical CGM data when using V3 API',
    category: 'Advanced',
  },

  // MyLife-specific
  ServiceUrl: {
    label: 'Service URL',
    description: 'MyLife service endpoint URL',
    category: 'Advanced',
  },
  EnableMealCarbConsolidation: {
    label: 'Consolidate Meal Carbs',
    description: 'Combine multiple carb entries from the same meal',
    category: 'Advanced',
  },
  EnableTempBasalConsolidation: {
    label: 'Consolidate Temp Basals',
    description: 'Combine consecutive temporary basal segments',
    category: 'Advanced',
  },
  TempBasalConsolidationWindowMinutes: {
    label: 'Temp Basal Window',
    description: 'Time window in minutes for consolidating temp basals',
    category: 'Advanced',
  },
  AppPlatform: {
    label: 'App Platform',
    description: 'Mobile platform identifier (iOS/Android)',
    category: 'Advanced',
  },
  AppVersion: {
    label: 'App Version',
    description: 'Mobile app version string',
    category: 'Advanced',
  },

  // MyFitnessPal-specific
  LookbackDays: {
    label: 'Lookback Days',
    description: 'Number of days of historical data to retrieve',
    category: 'Sync',
  },
  LastFullWalkAt: {
    label: 'Last Full Walk',
    description: 'When the diary was last read back to its first entry',
    category: 'Advanced',
  },

  // CareLink-specific
  CountryCode: {
    label: 'Country Code',
    description: 'Two-letter country code of the CareLink account',
    category: 'General',
  },
  LanguageCode: {
    label: 'Language Code',
    description: 'Two-letter language code of the CareLink account',
    category: 'General',
  },

  // Tandem-specific
  PumpSerialNumber: {
    label: 'Pump Serial Number',
    description: 'Serial number of the pump to follow when the account has more than one',
    category: 'General',
  },
  FetchAllEventTypes: {
    label: 'Fetch All Event Types',
    description: 'Read every pump history event type rather than the default filtered set',
    category: 'Advanced',
  },
  IgnoreZeroUnitBasal: {
    label: 'Ignore Zero-Unit Basal',
    description: 'Skip basal entries that resolve to a near-zero rate',
    category: 'Advanced',
  },
  // OAuth and Webhooks
  AccessToken: {
    label: 'Access Token',
    description: 'OAuth access token for the service',
    category: 'Credentials',
  },
  WebhookEnabled: {
    label: 'Webhook Enabled',
    description: 'Enable real-time updates via webhooks',
    category: 'Sync',
  },
  WebhookSecret: {
    label: 'Webhook Secret',
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
    label: 'Enable Write-back',
    description: 'Allow writing data back to the source service',
    category: 'Advanced',
  },
  WriteBackBatchSize: {
    label: 'Write-back Batch Size',
    description: 'Number of records to write back per batch',
    category: 'Advanced',
  },
  GlucoseProcessing: {
    label: 'Glucose Processing',
    description: 'How the connector labels its glucose readings (smoothed or unsmoothed)',
    category: 'General',
  },
} satisfies Record<string, PropertyMeta>;

/** String key names, taken from the entries above. */
export type ConnectorPropertyKeyName = keyof typeof connectorPropertyMeta;

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
  if (key in connectorPropertyMeta) {
    return connectorPropertyMeta[key as ConnectorPropertyKeyName];
  }

  // Convert camelCase to PascalCase for lookup (schema keys are camelCased)
  const pascalKey = key.charAt(0).toUpperCase() + key.slice(1);
  if (pascalKey in connectorPropertyMeta) {
    return connectorPropertyMeta[pascalKey as ConnectorPropertyKeyName];
  }

  return {
    label: formatPropertyName(key),
    description: '',
    category: 'General',
  };
}
