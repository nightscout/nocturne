/**
 * Clock Builder Types and Constants
 *
 * This module contains all type definitions and configuration constants
 * for the clock face builder UI.
 */

import type { ClockElement, ClockSettings } from "$lib/api/api-client";

// Element type for the clock face builder
export type ClockElementType =
  | "sg"
  | "delta"
  | "arrow"
  | "age"
  | "summary"
  | "forecast"
  | "iob"
  | "cob"
  | "basal"
  | "tracker"
  | "trackers"
  | "time"
  | "text"
  | "chart";

// Text-based element types (use unified text rendering)
export const TEXT_ELEMENT_TYPES: string[] = [
  "sg",
  "delta",
  "arrow",
  "age",
  "time",
  "iob",
  "cob",
  "basal",
  "forecast",
  "summary",
  "text",
  "tracker",
  "trackers",
];

// Element info for the builder palette
export interface ElementInfo {
  type: ClockElementType;
  name: string;
  description: string;
  defaultSize: number;
  minSize: number;
  maxSize: number;
  hasHoursOption?: boolean;
  hasFormatOption?: boolean;
  hasMinutesAheadOption?: boolean;
  hasTrackerOptions?: boolean;
  hasTrackersOptions?: boolean;
  hasTextOptions?: boolean;
  hasChartOptions?: boolean;
  defaultDynamicColor?: boolean;
}

export const ELEMENT_INFO: Record<ClockElementType, ElementInfo> = {
  sg: {
    type: "sg",
    name: "BG",
    description: "Blood glucose",
    defaultSize: 40,
    minSize: 8,
    maxSize: 500,
    defaultDynamicColor: true,
  },
  delta: {
    type: "delta",
    name: "Delta",
    description: "Change",
    defaultSize: 14,
    minSize: 8,
    maxSize: 500,
    defaultDynamicColor: true,
  },
  arrow: {
    type: "arrow",
    name: "Arrow",
    description: "Trend",
    defaultSize: 25,
    minSize: 8,
    maxSize: 500,
    defaultDynamicColor: true,
  },
  age: {
    type: "age",
    name: "Age",
    description: "Reading age",
    defaultSize: 10,
    minSize: 8,
    maxSize: 500,
  },
  summary: {
    type: "summary",
    name: "Summary",
    description: "Time in range",
    defaultSize: 12,
    minSize: 8,
    maxSize: 500,
    hasHoursOption: true,
  },
  forecast: {
    type: "forecast",
    name: "Forecast",
    description: "Predicted BG",
    defaultSize: 20,
    minSize: 8,
    maxSize: 500,
    hasMinutesAheadOption: true,
    defaultDynamicColor: true,
  },
  iob: {
    type: "iob",
    name: "IOB",
    description: "Insulin on board",
    defaultSize: 14,
    minSize: 8,
    maxSize: 500,
  },
  cob: {
    type: "cob",
    name: "COB",
    description: "Carbs on board",
    defaultSize: 14,
    minSize: 8,
    maxSize: 500,
  },
  basal: {
    type: "basal",
    name: "Basal",
    description: "Basal rate",
    defaultSize: 14,
    minSize: 8,
    maxSize: 500,
  },
  tracker: {
    type: "tracker",
    name: "Tracker",
    description: "Single tracker",
    defaultSize: 14,
    minSize: 8,
    maxSize: 500,
    hasTrackerOptions: true,
  },
  trackers: {
    type: "trackers",
    name: "Trackers",
    description: "All trackers",
    defaultSize: 12,
    minSize: 8,
    maxSize: 500,
    hasTrackersOptions: true,
  },
  time: {
    type: "time",
    name: "Time",
    description: "Current time",
    defaultSize: 20,
    minSize: 8,
    maxSize: 500,
    hasFormatOption: true,
  },
  text: {
    type: "text",
    name: "Text",
    description: "Custom text",
    defaultSize: 14,
    minSize: 8,
    maxSize: 500,
    hasTextOptions: true,
  },
  chart: {
    type: "chart",
    name: "Chart",
    description: "Full glucose chart",
    defaultSize: 200,
    minSize: 50,
    maxSize: 1000,
    hasChartOptions: true,
    hasHoursOption: true,
  },
};

export interface ElementGroup {
  name: string;
  types: ClockElementType[];
}

export const ELEMENT_GROUPS: ElementGroup[] = [
  {
    name: "Glucose",
    types: ["sg", "delta", "arrow", "age", "summary"],
  },
  {
    name: "Loop",
    types: ["forecast", "iob", "cob", "basal"],
  },
  { name: "Trackers", types: ["tracker", "trackers"] },
  { name: "Display", types: ["time", "text", "chart"] },
];

export interface SelectOption {
  value: string;
  label: string;
}

export const FONT_OPTIONS: SelectOption[] = [
  { value: "system", label: "System" },
  { value: "mono", label: "Monospace" },
  { value: "serif", label: "Serif" },
  { value: "sans", label: "Sans-serif" },
];

export const FONT_WEIGHT_OPTIONS: SelectOption[] = [
  { value: "normal", label: "Normal" },
  { value: "medium", label: "Medium" },
  { value: "semibold", label: "Semibold" },
  { value: "bold", label: "Bold" },
];

export const VISIBILITY_OPTIONS: SelectOption[] = [
  { value: "always", label: "Always show" },
  { value: "info", label: "Info or higher" },
  { value: "warn", label: "Warning or higher" },
  { value: "hazard", label: "Hazard or higher" },
  { value: "urgent", label: "Urgent only" },
];

export const TRACKER_SHOW_OPTIONS: SelectOption[] = [
  { value: "name", label: "Name" },
  { value: "icon", label: "Icon" },
  { value: "remaining", label: "Time remaining" },
  { value: "urgency", label: "Urgency badge" },
];

export const TRACKER_CATEGORIES = [
  "Sensor",
  "Cannula",
  "Reservoir",
  "Battery",
  "Consumable",
  "Appointment",
  "Reminder",
  "Custom",
] as const;

export type TrackerCategory = (typeof TRACKER_CATEGORIES)[number];

export interface ChartFeatureOption {
  key: string;
  label: string;
  defaultValue: boolean;
}

export const CHART_FEATURE_OPTIONS: ChartFeatureOption[] = [
  { key: "showBolus", label: "Bolus markers", defaultValue: true },
  { key: "showCarbs", label: "Carb markers", defaultValue: true },
  { key: "showIob", label: "IOB track", defaultValue: false },
  { key: "showCob", label: "COB track", defaultValue: false },
  { key: "showBasal", label: "Basal track", defaultValue: false },
  { key: "showPredictions", label: "Predictions", defaultValue: false },
  { key: "showDeviceEvents", label: "Device events", defaultValue: false },
  { key: "showTrackers", label: "Tracker markers", defaultValue: false },
  { key: "showAlarms", label: "Alarms", defaultValue: false },
];

// Internal types with IDs for the builder
export interface InternalElement extends ClockElement {
  _id: string;
}

export interface InternalRow {
  _id: string;
  elements: InternalElement[];
}

export interface InternalConfig {
  rows: InternalRow[];
  settings: ClockSettings;
}

// Drag state for element reordering
export interface DragState {
  rowIndex: number;
  elementIndex: number;
  element: InternalElement;
}

// Default settings
export const DEFAULT_SETTINGS: ClockSettings = {
  bgColor: false,
  staleMinutes: 13,
  alwaysShowTime: false,
  backgroundOpacity: 100,
};
