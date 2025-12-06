/**
 * UI Settings Configuration Types
 * These types mirror the backend UISettingsConfiguration model
 * and are used by the settings pages to consume data from the API.
 */

// Re-export alarm profile types for convenience
export type {
  AlarmProfileConfiguration,
  UserAlarmConfiguration,
  AlarmTriggerType,
  AlarmPriority,
  AlarmAudioSettings,
  AlarmVibrationSettings,
  AlarmVisualSettings,
  AlarmSnoozeSettings,
  AlarmReraiseSettings,
  SmartSnoozeSettings,
  AlarmScheduleSettings,
  TimeRange,
  QuietHoursConfiguration,
  CustomSoundReference,
  EmergencyContactConfig,
  NotificationChannelsConfig,
  ChannelConfig,
  PushoverChannelConfig,
} from "$lib/types/alarm-profile";

// Device Settings
export interface ConnectedDevice {
  id: string;
  name: string;
  type: string; // "cgm", "pump", "meter"
  status: string; // "connected", "disconnected", "error"
  battery?: number;
  lastSync?: string;
  serialNumber?: string;
}

export interface CgmConfiguration {
  dataSourcePriority: string; // "cgm", "meter", "average"
  sensorWarmupHours: number;
}

export interface DeviceSettings {
  connectedDevices: ConnectedDevice[];
  autoConnect: boolean;
  showRawData: boolean;
  uploadEnabled: boolean;
  cgmConfiguration: CgmConfiguration;
}

// Algorithm Settings
export interface PredictionSettings {
  enabled: boolean;
  minutes: number;
  model: string; // "ar2", "linear", "iob", "cob", "uam"
}

export interface AutosensSettings {
  enabled: boolean;
  min: number;
  max: number;
}

export interface CarbAbsorptionSettings {
  defaultMinutes: number;
  minRateGramsPerHour: number;
}

export interface LoopSettings {
  enabled: boolean;
  mode: string; // "open", "closed"
  maxBasalRate: number;
  maxBolus: number;
  smbEnabled: boolean;
  uamEnabled: boolean;
}

export interface SafetyLimits {
  maxIOB: number;
  maxDailyBasalMultiplier: number;
}

export interface AlgorithmSettings {
  prediction: PredictionSettings;
  autosens: AutosensSettings;
  carbAbsorption: CarbAbsorptionSettings;
  loop: LoopSettings;
  safetyLimits: SafetyLimits;
}

// Feature Settings
export interface DisplaySettings {
  nightMode: boolean;
  theme: string;
  timeFormat: string;
  units: string;
  showRawBG: boolean;
  focusHours: number;
}

export interface DashboardWidgets {
  glucoseChart: boolean;
  statistics: boolean;
  treatments: boolean;
  predictions: boolean;
  agp: boolean;
  dailyStats: boolean;
  batteryStatus: boolean;
}

export interface BatteryDisplaySettings {
  warnThreshold: number;
  urgentThreshold: number;
  enableAlerts: boolean;
  recentMinutes: number;
  showVoltage: boolean;
  showStatistics: boolean;
}

export interface PluginSettings {
  enabled: boolean;
  description: string;
}

export interface FeatureSettings {
  display: DisplaySettings;
  dashboardWidgets: DashboardWidgets;
  plugins: Record<string, PluginSettings>;
  battery: BatteryDisplaySettings;
}

// Notification Settings
export interface AlarmConfig {
  enabled: boolean;
  threshold: number;
  sound: string;
  repeatMinutes: number;
  snoozeOptions: number[];
}

export interface StaleDataAlarm {
  enabled: boolean;
  warningMinutes: number;
  urgentMinutes: number;
  sound: string;
}

export interface AlarmSettings {
  urgentHigh: AlarmConfig;
  high: AlarmConfig;
  low: AlarmConfig;
  urgentLow: AlarmConfig;
  staleData: StaleDataAlarm;
}

export interface QuietHoursSettings {
  enabled: boolean;
  startTime: string;
  endTime: string;
}

export interface NotificationChannel {
  enabled: boolean;
  label: string;
}

export interface NotificationChannels {
  push: NotificationChannel;
  email: NotificationChannel;
  sms: NotificationChannel;
}

export interface EmergencyContact {
  id: string;
  name: string;
  phone: string;
  notifyOnUrgent: boolean;
}

export interface NotificationSettings {
  alarmsEnabled: boolean;
  soundEnabled: boolean;
  vibrationEnabled: boolean;
  volume: number;
  alarms: AlarmSettings;
  quietHours: QuietHoursSettings;
  channels: NotificationChannels;
  emergencyContacts: EmergencyContact[];
  /** New xDrip+-style alarm configuration. When present, takes precedence over legacy alarms. */
  alarmConfiguration?: import("$lib/types/alarm-profile").UserAlarmConfiguration;
}

// Services Settings
export interface ConnectedService {
  id: string;
  name: string;
  type: string; // "cgm", "pump", "data", "food"
  description: string;
  status: string; // "connected", "disconnected", "error", "syncing"
  lastSync?: string;
  icon: string;
  configured: boolean;
  enabled: boolean;
}

export interface AvailableService {
  id: string;
  name: string;
  type: string;
  description: string;
  icon: string;
}

export interface SyncSettings {
  autoSync: boolean;
  syncOnAppOpen: boolean;
  backgroundRefresh: boolean;
}

export interface ServicesSettings {
  connectedServices: ConnectedService[];
  availableServices: AvailableService[];
  syncSettings: SyncSettings;
}

// Complete UI Settings Configuration
export interface UISettingsConfiguration {
  devices: DeviceSettings;
  algorithm: AlgorithmSettings;
  features: FeatureSettings;
  notifications: NotificationSettings;
  services: ServicesSettings;
}
