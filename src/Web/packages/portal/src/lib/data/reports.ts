/**
 * The reports the app ships today, in the order and grouping of its reports
 * page (src/Web/packages/app/src/lib/navigation/report-navigation.ts). Only
 * reports marked available there belong here; the marketing pages quote this
 * list and its length, so it must not run ahead of the product.
 */
export type ReportPreview =
  | "summary"
  | "agp"
  | "distribution"
  | "quality"
  | "year"
  | "readings"
  | "day"
  | "week"
  | "month"
  | "comparison"
  | "steps"
  | "heart"
  | "sleep"
  | "treatments"
  | "basal"
  | "insulin"
  | "site"
  | "idp"
  | "battery";

export interface Report {
  name: string;
  /** Sidebar label, as the app abbreviates it. */
  short: string;
  group: "The Big Picture" | "Patterns & Trends" | "Lifestyle Impact" | "Treatment Insights";
  preview: ReportPreview;
}

export const REPORTS: readonly Report[] = [
  { name: "Executive Summary", short: "Summary", group: "The Big Picture", preview: "summary" },
  { name: "Glucose Profile (AGP)", short: "AGP", group: "The Big Picture", preview: "agp" },
  { name: "Glucose Distribution", short: "Distribution", group: "The Big Picture", preview: "distribution" },
  { name: "Data Quality", short: "Data Quality", group: "The Big Picture", preview: "quality" },
  { name: "Data Overview", short: "Year Overview", group: "Patterns & Trends", preview: "year" },
  { name: "Day-by-Day View", short: "Readings", group: "Patterns & Trends", preview: "readings" },
  { name: "Day in Review", short: "Day in Review", group: "Patterns & Trends", preview: "day" },
  { name: "Week to Week", short: "Week to Week", group: "Patterns & Trends", preview: "week" },
  { name: "Month to Month", short: "Month to Month", group: "Patterns & Trends", preview: "month" },
  { name: "Comparison", short: "Comparison", group: "Patterns & Trends", preview: "comparison" },
  { name: "Step Count", short: "Steps", group: "Lifestyle Impact", preview: "steps" },
  { name: "Heart Rate", short: "Heart Rate", group: "Lifestyle Impact", preview: "heart" },
  { name: "Sleep & Overnight", short: "Sleep", group: "Lifestyle Impact", preview: "sleep" },
  { name: "Treatment Log", short: "Treatments", group: "Treatment Insights", preview: "treatments" },
  { name: "Basal Rate Analysis", short: "Basal Analysis", group: "Treatment Insights", preview: "basal" },
  { name: "Insulin Delivery", short: "Insulin Delivery", group: "Treatment Insights", preview: "insulin" },
  { name: "Site Change Impact", short: "Site Changes", group: "Treatment Insights", preview: "site" },
  { name: "Insulin Dosing Profile", short: "IDP", group: "Treatment Insights", preview: "idp" },
  { name: "Battery", short: "Battery", group: "Treatment Insights", preview: "battery" },
] as const;

export const AVAILABLE_REPORT_COUNT = REPORTS.length;
