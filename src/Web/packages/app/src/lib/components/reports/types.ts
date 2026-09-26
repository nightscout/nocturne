// Store common type definitions for report components

import type { Entry, GlucoseAnalytics, Treatment, TreatmentSummary } from '$lib/api';

export interface DayToDayDailyData {
  date: string; // YYYY-MM-DD format
  readingsCount: number;
  analytics: GlucoseAnalytics; // API GlucoseAnalytics type
  trend: "rising" | "falling" | "stable";
  glucoseData: Entry[]; // Array of glucose readings for the day
  treatments: Treatment[]; // Array of treatments for the day
  treatmentSummary: TreatmentSummary;
}

/**
 * Thresholds for glucose targets used in reports
 */
export interface Thresholds {
  low: number;
  targetBottom: number;
  targetTop: number;
  tightTargetTop: number;
  high: number;
}

/**
 * Treatment data item for display in treatment events
 */
export interface TreatmentDataItem {
  eventType: string;
  timestamp: string | number;
  insulin?: number;
  carbs?: number;
}
