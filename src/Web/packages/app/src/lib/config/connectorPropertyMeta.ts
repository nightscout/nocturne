/**
 * Frontend translation dictionary for connector property keys.
 * Maps backend ConnectorPropertyKey enum values to localized labels, descriptions, and categories.
 */

// Local type definition for connector property keys
export enum ConnectorPropertyKey {
  TimezoneOffset = "TimezoneOffset",
  Enabled = "Enabled",
  MaxRetryAttempts = "MaxRetryAttempts",
  BatchSize = "BatchSize",
  SyncIntervalMinutes = "SyncIntervalMinutes",
  SyncGlucose = "SyncGlucose",
  SyncManualBG = "SyncManualBG",
  SyncBoluses = "SyncBoluses",
  SyncCarbIntake = "SyncCarbIntake",
  SyncBolusCalculations = "SyncBolusCalculations",
  SyncNotes = "SyncNotes",
  SyncDeviceEvents = "SyncDeviceEvents",
  SyncStateSpans = "SyncStateSpans",
  SyncProfiles = "SyncProfiles",
  SyncDeviceStatus = "SyncDeviceStatus",
  SyncActivity = "SyncActivity",
  SyncFood = "SyncFood",
  Username = "Username",
  Password = "Password",
  Email = "Email",
  Server = "Server",
  Region = "Region",
  PatientId = "PatientId",
  UserId = "UserId",
  Url = "Url",
  ApiSecret = "ApiSecret",
  MaxCount = "MaxCount",
  UseV3Api = "UseV3Api",
  V3IncludeCgmBackfill = "V3IncludeCgmBackfill",
  ServiceUrl = "ServiceUrl",
  EnableMealCarbConsolidation = "EnableMealCarbConsolidation",
  EnableTempBasalConsolidation = "EnableTempBasalConsolidation",
  TempBasalConsolidationWindowMinutes = "TempBasalConsolidationWindowMinutes",
  AppPlatform = "AppPlatform",
  AppVersion = "AppVersion",
  LookbackDays = "LookbackDays",
  AccessToken = "AccessToken",
  WebhookEnabled = "WebhookEnabled",
  WebhookSecret = "WebhookSecret",
  WriteBackEnabled = "WriteBackEnabled",
  WriteBackBatchSize = "WriteBackBatchSize",
  GlucoseProcessing = "GlucoseProcessing",
}

/** String key names derived from the generated enum */
type ConnectorPropertyKeyName = keyof typeof ConnectorPropertyKey;

export type PropertyCategory = 'General' | 'Credentials' | 'Sync' | 'Advanced';

export type PropertyMeta = {
  label: string;
  description: string;
  category: PropertyCategory;
};

export const connectorPropertyMeta: Record<ConnectorPropertyKeyName, PropertyMeta> = {
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
};

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
