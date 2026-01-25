import { randomUUID } from "$lib/utils";

/**
 * xDrip+-inspired Alarm Profile Types
 *
 * These types define a comprehensive alarm configuration system that supports:
 * - Named alarms with custom descriptions
 * - Custom audio with ascending volume
 * - Smart snooze (auto-extend when trending correct direction)
 * - Delayed raise (persistence minutes)
 * - Time-of-day scheduling
 * - Per-alarm sound and vibration settings
 * - Screen flash on alarm
 * - Re-raise for unacknowledged alarms
 */

/** Types of alarm triggers */
export type AlarmTriggerType =
  | "High"
  | "Low"
  | "UrgentHigh"
  | "UrgentLow"
  | "RisingFast"
  | "FallingFast"
  | "StaleData"
  | "ForecastLow"
  | "Custom";

/** Priority levels for alarms */
export type AlarmPriority = "Low" | "Normal" | "High" | "Critical";

/** Audio settings for an alarm */
export interface AlarmAudioSettings {
  /** Whether sound is enabled for this alarm */
  enabled: boolean;
  /** Sound identifier - can be built-in or custom audio file reference */
  soundId: string;
  /** Path to custom audio file (if using custom sound) */
  customSoundUrl?: string;
  /** Whether to use ascending volume (starts quiet, gets louder) */
  ascendingVolume: boolean;
  /** Starting volume percentage (0-100) when ascending volume is enabled */
  startVolume: number;
  /** Maximum volume percentage (0-100) */
  maxVolume: number;
  /** Seconds to reach max volume when ascending */
  ascendDurationSeconds: number;
  /** How many times to repeat the sound (0 = until acknowledged) */
  repeatCount: number;
}

/** Vibration settings for an alarm */
export interface AlarmVibrationSettings {
  /** Whether vibration is enabled for this alarm */
  enabled: boolean;
  /** Vibration pattern: "short", "long", "sos", "continuous", or custom pattern */
  pattern: string;
  /** Custom vibration pattern as array of milliseconds [vibrate, pause, vibrate, pause, ...] */
  customPattern?: number[];
}

/** Visual settings for an alarm (screen flash, colors) */
export interface AlarmVisualSettings {
  /** Whether to flash the screen when alarm triggers */
  screenFlash: boolean;
  /** Color to use for screen flash (hex format) */
  flashColor: string;
  /** Flash frequency in milliseconds */
  flashIntervalMs: number;
  /** Whether to show a persistent banner until acknowledged */
  persistentBanner: boolean;
  /** Whether to turn on the screen when alarm triggers */
  wakeScreen: boolean;
  /** Whether to show emergency contacts during alarm overlay */
  showEmergencyContacts: boolean;
  /** Custom instructions for emergency contacts */
  emergencyInstructions?: string;
}

/** Snooze settings for an alarm */
export interface AlarmSnoozeSettings {
  /** Default snooze duration in minutes */
  defaultMinutes: number;
  /** Available snooze duration options in minutes */
  options: number[];
  /** Maximum snooze duration allowed in minutes */
  maxMinutes: number;
  /** Maximum number of times alarm can be snoozed */
  maxSnoozeCount?: number;
}

/** Settings for re-raising unacknowledged alarms */
export interface AlarmReraiseSettings {
  /** Whether to automatically re-raise if not acknowledged */
  enabled: boolean;
  /** Re-raise interval in minutes */
  intervalMinutes: number;
  /** Whether to escalate (get louder) on each re-raise */
  escalate: boolean;
  /** Volume increase per escalation (percentage points) */
  escalationVolumeStep: number;
}

/** Smart snooze settings - auto-extend snooze when trending in right direction */
export interface SmartSnoozeSettings {
  /** Whether smart snooze is enabled */
  enabled: boolean;
  /** For high alarms: auto-extend snooze if glucose is falling. For low alarms: auto-extend snooze if glucose is rising */
  extendWhenTrendingCorrect: boolean;
  /** Minimum delta (mg/dL per 5 min) to consider "trending correct" */
  minDeltaThreshold: number;
  /** Extension duration in minutes when trending correct */
  extensionMinutes: number;
  /** Maximum total snooze time with extensions */
  maxTotalMinutes: number;
}

/** A time range for scheduling */
export interface TimeRange {
  /** Start time in HH:mm format (24-hour) */
  startTime: string;
  /** End time in HH:mm format (24-hour) */
  endTime: string;
}

/** Time-of-day schedule for when alarm is active */
export interface AlarmScheduleSettings {
  /** Whether scheduling is enabled (if false, alarm is always active) */
  enabled: boolean;
  /** Time ranges when this alarm is active */
  activeRanges: TimeRange[];
  /** Days of week when alarm is active (0=Sunday, 6=Saturday). If empty, active every day */
  activeDays?: number[];
  /** Timezone for schedule interpretation */
  timezone?: string;
}

/**
 * xDrip+-inspired alarm profile configuration.
 * Each user can have multiple alarm profiles with complex, customizable behavior.
 */
export interface AlarmProfileConfiguration {
  /** Unique identifier for this alarm profile */
  id: string;
  /** User-defined name for this alarm (e.g., "Urgent Low", "Nighttime High") */
  name: string;
  /** Description of what this alarm is for */
  description?: string;
  /** Whether this alarm is currently enabled */
  enabled: boolean;
  /** The type of alarm condition to check */
  alarmType: AlarmTriggerType;
  /** Glucose threshold that triggers this alarm (in mg/dL) */
  threshold: number;
  /** For range alarms, the upper bound (threshold is lower bound) */
  thresholdHigh?: number;
  /** Lead time in minutes for forecast alerts (ForecastLow) */
  forecastLeadTimeMinutes?: number;
  /** Delayed raise - only trigger if above/below threshold for this many minutes */
  persistenceMinutes: number;
  /** Audio configuration for this alarm */
  audio: AlarmAudioSettings;
  /** Vibration settings for this alarm */
  vibration: AlarmVibrationSettings;
  /** Visual settings (screen flash, colors) */
  visual: AlarmVisualSettings;
  /** Snooze behavior configuration */
  snooze: AlarmSnoozeSettings;
  /** Re-raise/repeat configuration for unacknowledged alarms */
  reraise: AlarmReraiseSettings;
  /** Smart snooze - auto-extend snooze if glucose is trending in the right direction */
  smartSnooze: SmartSnoozeSettings;
  /** Time-of-day schedule - when this alarm is active */
  schedule: AlarmScheduleSettings;
  /** Priority level for this alarm (affects notification behavior) */
  priority: AlarmPriority;
  /** Override quiet hours for this alarm */
  overrideQuietHours: boolean;
  /** Order for display in UI (lower numbers first) */
  displayOrder: number;
  /** Timestamp when this profile was created */
  createdAt: string;
  /** Timestamp when this profile was last modified */
  updatedAt: string;
}

/** Enhanced quiet hours configuration */
export interface QuietHoursConfiguration {
  enabled: boolean;
  startTime: string;
  endTime: string;
  /** Allow critical alarms during quiet hours */
  allowCritical: boolean;
  /** Reduce volume during quiet hours instead of silencing */
  reduceVolume: boolean;
  /** Volume level during quiet hours (percentage) */
  quietVolume: number;
}

/** Reference to a custom uploaded sound */
export interface CustomSoundReference {
  id: string;
  name: string;
  url: string;
  durationSeconds?: number;
  uploadedAt: string;
}

/** Enhanced emergency contact with more notification options */
export interface EmergencyContactConfig {
  id: string;
  name: string;
  phone?: string;
  email?: string;
  /** Only notify for critical priority alarms */
  criticalOnly: boolean;
  /** Delay before notifying (allows user to acknowledge first) */
  delayMinutes: number;
  enabled: boolean;
}

/** Base channel configuration */
export interface ChannelConfig {
  enabled: boolean;
  /** Minimum priority level to send through this channel */
  minPriority: AlarmPriority;
}

/** Pushover-specific channel configuration */
export interface PushoverChannelConfig extends ChannelConfig {
  userKey?: string;
  apiToken?: string;
}

/** Notification channels configuration */
export interface NotificationChannelsConfig {
  push: ChannelConfig;
  email: ChannelConfig;
  sms: ChannelConfig;
  pushover?: PushoverChannelConfig;
}

/**
 * Complete user alarm configuration containing all profiles and global settings.
 * This is the root object stored in JSONB.
 */
export interface UserAlarmConfiguration {
  /** Version for migration purposes */
  version: number;
  /** Global master switch for all alarms */
  enabled: boolean;
  /** Global sound enabled setting */
  soundEnabled: boolean;
  /** Global vibration enabled setting */
  vibrationEnabled: boolean;
  /** Global volume level (0-100) */
  globalVolume: number;
  /** List of all configured alarm profiles */
  profiles: AlarmProfileConfiguration[];
  /** Quiet hours configuration */
  quietHours: QuietHoursConfiguration;
  /** Custom sounds uploaded by the user */
  customSounds: CustomSoundReference[];
  /** Emergency contacts to notify for urgent alarms */
  emergencyContacts: EmergencyContactConfig[];
  /** Notification channels configuration */
  channels: NotificationChannelsConfig;
}

/** Built-in sound presets */
export interface SoundPreset {
  id: string;
  name: string;
  description: string;
}

export const BUILT_IN_SOUNDS: SoundPreset[] = [
  { id: "alarm-default", name: "Default Alarm", description: "Standard alarm sound" },
  { id: "alarm-urgent", name: "Urgent Alarm", description: "Loud, attention-grabbing alarm" },
  { id: "alarm-high", name: "High Alert", description: "Warning tone for high glucose" },
  { id: "alarm-low", name: "Low Alert", description: "Warning tone for low glucose" },
  { id: "alert", name: "Alert", description: "General alert sound" },
  { id: "chime", name: "Chime", description: "Pleasant chime" },
  { id: "bell", name: "Bell", description: "Bell ring" },
  { id: "siren", name: "Siren", description: "Emergency siren" },
  { id: "beep", name: "Beep", description: "Simple beep" },
  { id: "soft", name: "Soft", description: "Gentle, quiet notification" },
];

/** Labels for alarm trigger types */
export const ALARM_TYPE_LABELS: Record<AlarmTriggerType, string> = {
  High: "High",
  Low: "Low",
  UrgentHigh: "Urgent High",
  UrgentLow: "Urgent Low",
  RisingFast: "Rising Fast",
  FallingFast: "Falling Fast",
  StaleData: "Stale Data",
  ForecastLow: "Forecast Low",
  Custom: "Custom Range",
};

/** Labels for alarm priorities */
export const PRIORITY_LABELS: Record<AlarmPriority, string> = {
  Low: "Low",
  Normal: "Normal",
  High: "High",
  Critical: "Critical",
};

const ALARM_TYPE_ALIASES: Record<string, AlarmTriggerType> = {
  "urgent high": "UrgentHigh",
  "urgent low": "UrgentLow",
  "rising fast": "RisingFast",
  "falling fast": "FallingFast",
  "stale data": "StaleData",
  "forecast low": "ForecastLow",
  "custom range": "Custom",
  "custom": "Custom",
  "high": "High",
  "low": "Low",
  "élevé": "High",
  "eleve": "High",
  "haut": "High",
  "haute": "High",
  "bas": "Low",
  "basse": "Low",
  "faible": "Low",
  "urgent élevé": "UrgentHigh",
  "urgent eleve": "UrgentHigh",
  "urgent haut": "UrgentHigh",
  "urgent bas": "UrgentLow",
  "hausse rapide": "RisingFast",
  "baisse rapide": "FallingFast",
  "données obsolètes": "StaleData",
  "donnees obsoletes": "StaleData",
  "prévision basse": "ForecastLow",
  "prevision basse": "ForecastLow",
  "personnalisé": "Custom",
  "personnalise": "Custom",
};

export function normalizeAlarmType(value: string | null | undefined): AlarmTriggerType {
  if (!value) {
    return "High";
  }

  if ((value as AlarmTriggerType) in ALARM_TYPE_LABELS) {
    return value as AlarmTriggerType;
  }

  const lowered = value.trim().toLowerCase();
  if (ALARM_TYPE_ALIASES[lowered]) {
    return ALARM_TYPE_ALIASES[lowered];
  }

  const labelMatch = (Object.keys(ALARM_TYPE_LABELS) as AlarmTriggerType[]).find(
    (key) => ALARM_TYPE_LABELS[key].toLowerCase() === lowered
  );
  return labelMatch ?? "High";
}

export function normalizeAlarmPriority(
  value: string | null | undefined
): AlarmPriority {
  if (!value) {
    return "Normal";
  }

  if ((value as AlarmPriority) in PRIORITY_LABELS) {
    return value as AlarmPriority;
  }

  const lowered = value.trim().toLowerCase();
  const labelMatch = (Object.keys(PRIORITY_LABELS) as AlarmPriority[]).find(
    (key) => PRIORITY_LABELS[key].toLowerCase() === lowered
  );
  return labelMatch ?? "Normal";
}

/** Colors for alarm types in UI */
export const ALARM_TYPE_COLORS: Record<AlarmTriggerType, { bg: string; border: string; text: string }> = {
  UrgentHigh: { bg: "bg-red-50 dark:bg-red-950/20", border: "border-red-200 dark:border-red-900", text: "text-red-600" },
  High: { bg: "bg-orange-50 dark:bg-orange-950/20", border: "border-orange-200 dark:border-orange-900", text: "text-orange-600" },
  Low: { bg: "bg-yellow-50 dark:bg-yellow-950/20", border: "border-yellow-200 dark:border-yellow-900", text: "text-yellow-600" },
  UrgentLow: { bg: "bg-red-50 dark:bg-red-950/20", border: "border-red-200 dark:border-red-900", text: "text-red-600" },
  RisingFast: { bg: "bg-orange-50 dark:bg-orange-950/20", border: "border-orange-200 dark:border-orange-900", text: "text-orange-600" },
  FallingFast: { bg: "bg-blue-50 dark:bg-blue-950/20", border: "border-blue-200 dark:border-blue-900", text: "text-blue-600" },
  ForecastLow: { bg: "bg-yellow-50 dark:bg-yellow-950/20", border: "border-yellow-200 dark:border-yellow-900", text: "text-yellow-600" },
  StaleData: { bg: "bg-gray-50 dark:bg-gray-950/20", border: "border-gray-200 dark:border-gray-900", text: "text-gray-600" },
  Custom: { bg: "bg-purple-50 dark:bg-purple-950/20", border: "border-purple-200 dark:border-purple-900", text: "text-purple-600" },
};

/** Create a new alarm profile with default settings */
export function createDefaultAlarmProfile(
  type: AlarmTriggerType = "High",
  name?: string
): AlarmProfileConfiguration {
  const now = new Date().toISOString();
  const defaults = getDefaultsForType(type);

  return {
    id: randomUUID(),
    name: name ?? defaults.name,
    description: defaults.description,
    enabled: true,
    alarmType: type,
    threshold: defaults.threshold,
    thresholdHigh: undefined,
    forecastLeadTimeMinutes: defaults.forecastLeadTimeMinutes,
    persistenceMinutes: 0,
    audio: {
      enabled: true,
      soundId: defaults.soundId,
      customSoundUrl: undefined,
      ascendingVolume: false,
      startVolume: 20,
      maxVolume: 100,
      ascendDurationSeconds: 30,
      repeatCount: 0,
    },
    vibration: {
      enabled: true,
      pattern: "short",
      customPattern: undefined,
    },
    visual: {
      screenFlash: type === "UrgentHigh" || type === "UrgentLow",
      flashColor: defaults.flashColor,
      flashIntervalMs: 500,
      persistentBanner: true,
      wakeScreen: true,
      showEmergencyContacts: type === "UrgentHigh" || type === "UrgentLow",
    },
    snooze: {
      defaultMinutes: defaults.snoozeDefault,
      options: [5, 10, 15, 30, 60],
      maxMinutes: 120,
      maxSnoozeCount: undefined,
    },
    reraise: {
      enabled: true,
      intervalMinutes: defaults.reraiseMinutes,
      escalate: type === "UrgentHigh" || type === "UrgentLow",
      escalationVolumeStep: 20,
    },
    smartSnooze: {
      enabled: false,
      extendWhenTrendingCorrect: true,
      minDeltaThreshold: 5,
      extensionMinutes: 15,
      maxTotalMinutes: 60,
    },
    schedule: {
      enabled: false,
      activeRanges: [{ startTime: "00:00", endTime: "23:59" }],
      activeDays: undefined,
      timezone: undefined,
    },
    priority: defaults.priority,
    overrideQuietHours: type === "UrgentHigh" || type === "UrgentLow",
    displayOrder: 0,
    createdAt: now,
    updatedAt: now,
  };
}

/** Get default settings for each alarm type */
function getDefaultsForType(type: AlarmTriggerType): {
  name: string;
  description: string;
  threshold: number;
  soundId: string;
  flashColor: string;
  snoozeDefault: number;
  reraiseMinutes: number;
  priority: AlarmPriority;
  forecastLeadTimeMinutes?: number;
} {
  switch (type) {
    case "UrgentHigh":
      return {
        name: "Urgent High",
        description: "Critical high glucose alert",
        threshold: 250,
        soundId: "alarm-urgent",
        flashColor: "#ef4444",
        snoozeDefault: 15,
        reraiseMinutes: 5,
        priority: "Critical",
      };
    case "High":
      return {
        name: "High",
        description: "Above target range",
        threshold: 180,
        soundId: "alarm-high",
        flashColor: "#f97316",
        snoozeDefault: 30,
        reraiseMinutes: 15,
        priority: "Normal",
      };
    case "Low":
      return {
        name: "Low",
        description: "Below target range",
        threshold: 70,
        soundId: "alarm-low",
        flashColor: "#eab308",
        snoozeDefault: 15,
        reraiseMinutes: 10,
        priority: "High",
      };
    case "UrgentLow":
      return {
        name: "Urgent Low",
        description: "Critical low glucose alert",
        threshold: 55,
        soundId: "alarm-urgent",
        flashColor: "#ef4444",
        snoozeDefault: 5,
        reraiseMinutes: 5,
        priority: "Critical",
      };
    case "RisingFast":
      return {
        name: "Rising Fast",
        description: "Glucose rising rapidly",
        threshold: 3, // mg/dL per minute
        soundId: "alert",
        flashColor: "#f97316",
        snoozeDefault: 30,
        reraiseMinutes: 15,
        priority: "Normal",
      };
    case "FallingFast":
      return {
        name: "Falling Fast",
        description: "Glucose falling rapidly",
        threshold: 3, // mg/dL per minute
        soundId: "alert",
        flashColor: "#3b82f6",
        snoozeDefault: 15,
        reraiseMinutes: 10,
        priority: "High",
      };
    case "StaleData":
      return {
        name: "Stale Data",
        description: "No new readings received",
        threshold: 15, // minutes
        soundId: "alert",
        flashColor: "#6b7280",
        snoozeDefault: 30,
        reraiseMinutes: 15,
        priority: "Normal",
      };
    case "ForecastLow":
      return {
        name: "Forecast Low",
        description: "Predicted to go low soon",
        threshold: 80,
        soundId: "alarm-low",
        flashColor: "#eab308",
        snoozeDefault: 15,
        reraiseMinutes: 10,
        priority: "High",
        forecastLeadTimeMinutes: 30,
      };
    case "Custom":
      return {
        name: "Custom Alarm",
        description: "Custom glucose range alert",
        threshold: 100,
        soundId: "alarm-default",
        flashColor: "#8b5cf6",
        snoozeDefault: 30,
        reraiseMinutes: 15,
        priority: "Normal",
      };
    default:
      return {
        name: "High",
        description: "Above target range",
        threshold: 180,
        soundId: "alarm-high",
        flashColor: "#f97316",
        snoozeDefault: 30,
        reraiseMinutes: 15,
        priority: "Normal",
      };
  }
}

/** Create a default user alarm configuration */
export function createDefaultUserAlarmConfiguration(): UserAlarmConfiguration {
  return {
    version: 1,
    enabled: true,
    soundEnabled: true,
    vibrationEnabled: true,
    globalVolume: 80,
    profiles: [
      createDefaultAlarmProfile("UrgentHigh"),
      createDefaultAlarmProfile("High"),
      createDefaultAlarmProfile("Low"),
      createDefaultAlarmProfile("UrgentLow"),
    ],
    quietHours: {
      enabled: false,
      startTime: "22:00",
      endTime: "07:00",
      allowCritical: true,
      reduceVolume: true,
      quietVolume: 30,
    },
    customSounds: [],
    emergencyContacts: [],
    channels: {
      push: { enabled: true, minPriority: "Normal" },
      email: { enabled: false, minPriority: "High" },
      sms: { enabled: false, minPriority: "Critical" },
    },
  };
}
