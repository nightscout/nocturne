import type { DeviceStatus, Entry, Treatment } from "$lib/api";

export interface ServerSettings {
  name?: string;
  version?: string;
  apiEnabled?: boolean;
  careportalEnabled?: boolean;
  boluscalcEnabled?: boolean;
  head?: string;
  runtimeState?: string;
  settings?: {
    units?: string;
    timeFormat?: number;
    nightMode?: boolean;
    showRawbg?: string;
    customTitle?: string;
    theme?: string;
    alarmUrgentHigh?: boolean;
    alarmHigh?: boolean;
    alarmLow?: boolean;
    alarmUrgentLow?: boolean;
    alarmTimeagoWarn?: boolean;
    alarmTimeagoWarnMins?: number;
    alarmTimeagoUrgent?: boolean;
    alarmTimeagoUrgentMins?: number;
    language?: string;
    enable?: string;
    showPlugins?: string;
    alarmTypes?: string;
    editMode?: boolean;
    thresholds?: {
      bgHigh?: number;
      bgTargetTop?: number;
      bgTargetBottom?: number;
      bgLow?: number;
    };
    extendedSettings?: any;
  };
  extendedSettings?: any;
  authorized?: {
    role?: string[];
  };
}

export interface ClientThresholds {
  high: number;
  targetTop: number;
  targetBottom: number;
  low: number;
}

/**
 * Settings for dynamic browser title and favicon based on glucose values
 */
export interface TitleFaviconSettings {
  /** Master switch for title/favicon updates */
  enabled: boolean;
  /** Show glucose value in browser title */
  showBgValue: boolean;
  /** Show direction arrow in browser title */
  showDirection: boolean;
  /** Show delta in browser title */
  showDelta: boolean;
  /** Custom text prefix before BG value (e.g., "Nocturne") */
  customPrefix: string;
  /** Enable dynamic favicon generation */
  faviconEnabled: boolean;
  /** Show BG value on dynamic favicon */
  faviconShowBg: boolean;
  /** Color-code favicon background based on glucose status */
  faviconColorCoded: boolean;
  /** Flash title/favicon during active alarms (uses AlarmVisualSettings) */
  flashOnAlarm: boolean;
}

export interface ClientSettings {
  units: "mg/dl" | "mmol";
  timeFormat: 12 | 24;
  nightMode: boolean;
  showBGON: boolean;
  showIOB: boolean;
  showCOB: boolean;
  showBasal: boolean;
  showPlugins: string[];
  language: string;
  theme: string;
  alarmUrgentHigh: boolean;
  alarmUrgentHighMins: number[];
  alarmHigh: boolean;
  alarmHighMins: number[];
  alarmLow: boolean;
  alarmLowMins: number[];
  alarmUrgentLow: boolean;
  alarmUrgentLowMins: number[];
  alarmTimeagoWarn: boolean;
  alarmTimeagoWarnMins: number;
  alarmTimeagoUrgent: boolean;
  alarmTimeagoUrgentMins: number;
  showForecast: boolean;
  focusHours: number;
  heartbeat: number;
  baseURL: string;
  authDefaultRoles: string;
  thresholds: ClientThresholds;
  demoMode: DemoModeSettings;
  titleFavicon: TitleFaviconSettings;
}

export interface DemoModeSettings {
  enabled: boolean;
  realTimeUpdates: boolean;
  webSocketUrl: string;
  showDemoIndicators: boolean;
}

export interface Client {
  entries: Entry[];
  treatments: Treatment[];
  deviceStatus: DeviceStatus[];
  settings: ClientSettings;
  now: number;
  latestSGV?: Entry;
  isLoading: boolean;
  isConnected: boolean;
  alarmInProgress: boolean;
  currentAnnouncement?: {
    received: number;
    title: string;
    message: string;
  };
  brushExtent: [Date, Date];
  focusRangeMS: number;
  inRetroMode: boolean;
}

/**
 * Direction arrow mappings for glucose trend display
 */
export const directions = {
  "NONE": { label: "→", description: "No direction" },
  "DoubleUp": { label: "⇈", description: "Rising quickly" },
  "SingleUp": { label: "↑", description: "Rising" },
  "FortyFiveUp": { label: "↗", description: "Rising slowly" },
  "Flat": { label: "→", description: "Stable" },
  "FortyFiveDown": { label: "↘", description: "Falling slowly" },
  "SingleDown": { label: "↓", description: "Falling" },
  "DoubleDown": { label: "⇊", description: "Falling quickly" },
  "NOT COMPUTABLE": { label: "-", description: "Not computable" },
  "RATE OUT OF RANGE": { label: "⇕", description: "Rate out of range" },
} as const;

export function getDirectionInfo(
  direction: string
): (typeof directions)[keyof typeof directions] {
  if (direction in directions) {
    return directions[direction as keyof typeof directions];
  }
  return directions["NOT COMPUTABLE"];
}
