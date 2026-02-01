/**
 * Clock Builder Utility Functions
 *
 * This module contains helper functions for styling, color management,
 * and other utilities used by the clock face builder.
 */

import type { ClockElement, TrackerDefinitionDto } from "$lib/api/api-client";
import { ELEMENT_INFO, type ClockElementType, type InternalElement } from "./types";

/**
 * Get BG color based on glucose value
 */
export function getBgColor(bg: number): string {
  if (bg < 70) return "#ef4444";
  if (bg < 80) return "#eab308";
  if (bg > 250) return "#ef4444";
  if (bg > 180) return "#f97316";
  return "#22c55e";
}

/**
 * Get rotation degrees for Lucide arrow based on direction
 */
export function getDirectionRotation(direction: string): number {
  const rotations: Record<string, number> = {
    DoubleUp: 0,
    SingleUp: 0,
    FortyFiveUp: 45,
    Flat: 90,
    FortyFiveDown: 135,
    SingleDown: 180,
    DoubleDown: 180,
  };
  return rotations[direction] ?? 90;
}

/**
 * Check if direction is double arrow
 */
export function isDoubleArrow(direction: string): boolean {
  return direction === "DoubleUp" || direction === "DoubleDown";
}

/**
 * Get tracker name from definition ID
 */
export function getTrackerName(
  definitionId: string | undefined,
  trackerDefinitions: TrackerDefinitionDto[]
): string {
  if (!definitionId) return "Select tracker...";
  const def = trackerDefinitions.find((d) => d.id === definitionId);
  return def?.name ?? "Unknown";
}

/**
 * Get tracker definition by ID
 */
export function getTrackerDefinition(
  definitionId: string | undefined,
  trackerDefinitions: TrackerDefinitionDto[]
): TrackerDefinitionDto | null {
  if (!definitionId) return null;
  return trackerDefinitions.find((d) => d.id === definitionId) ?? null;
}

/**
 * Get font class from font option
 */
export function getFontClass(font: string | undefined): string {
  switch (font) {
    case "mono":
      return "font-mono";
    case "serif":
      return "font-serif";
    case "sans":
      return "font-sans";
    default:
      return "";
  }
}

/**
 * Get font weight class from weight option
 */
export function getFontWeightClass(weight: string | undefined): string {
  switch (weight) {
    case "normal":
      return "font-normal";
    case "medium":
      return "font-medium";
    case "semibold":
      return "font-semibold";
    case "bold":
      return "font-bold";
    default:
      return "font-medium";
  }
}

/**
 * Get element text color from style
 */
export function getElementColor(element: ClockElement, currentBG: number): string {
  const color = element.style?.color;
  if (color === "dynamic") return getBgColor(currentBG);
  return color || "#ffffff";
}

/**
 * Build custom CSS properties string from element.style.custom
 */
export function buildCustomCssString(element: ClockElement): string {
  const custom = element.style?.custom;
  if (!custom) return "";
  return Object.entries(custom)
    .map(([key, value]) => `${key}: ${value}`)
    .join("; ");
}

/**
 * Build inline style string from element.style (including custom properties)
 */
export function buildStyleString(element: ClockElement, currentBG: number): string {
  const style = element.style;
  const parts: string[] = [];

  // Font size from element.size
  const size =
    element.size ||
    ELEMENT_INFO[element.type as ClockElementType]?.defaultSize ||
    20;
  parts.push(`font-size: ${size * 0.8}px`);

  // Color
  parts.push(`color: ${getElementColor(element, currentBG)}`);

  // Opacity
  parts.push(`opacity: ${style?.opacity ?? 1.0}`);

  // Add any custom CSS properties
  const customCss = buildCustomCssString(element);
  if (customCss) {
    parts.push(customCss);
  }

  return parts.join("; ");
}

/**
 * Format time based on 12h/24h preference
 */
export function formatTime(format: string | undefined, currentTime: Date): string {
  const is24h = format === "24h";
  return currentTime.toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    hour12: !is24h,
  });
}

/**
 * Check if element is a text-based element (uses unified text rendering)
 */
export function isTextElement(type: string): boolean {
  const textTypes = [
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
  return textTypes.includes(type);
}

/**
 * Check if a tracker/trackers element would be hidden based on visibility threshold
 * In the builder we show a dashed border to indicate it wouldn't normally be visible
 */
export function isTrackerBelowThreshold(element: InternalElement): boolean {
  if (element.type !== "tracker" && element.type !== "trackers") return false;
  const threshold = element.visibilityThreshold;
  // "always" means always visible, so not below threshold
  if (!threshold || threshold === "always") return false;
  // For demo purposes in the builder, we simulate that trackers are at "info" level
  // So anything requiring warn/hazard/urgent would be hidden
  const thresholdOrder = ["always", "info", "warn", "hazard", "urgent"];
  const currentLevel = "info"; // Simulated current urgency level
  const thresholdIndex = thresholdOrder.indexOf(threshold);
  const currentIndex = thresholdOrder.indexOf(currentLevel);
  return thresholdIndex > currentIndex;
}

/**
 * Check if show option is checked for tracker elements
 */
export function isShowOptionChecked(
  show: string[] | undefined,
  option: string
): boolean {
  return show?.includes(option) ?? false;
}

/**
 * Check if category is checked for trackers element
 */
export function isCategoryChecked(
  categories: string[] | undefined,
  category: string
): boolean {
  if (!categories || categories.length === 0) return true;
  return categories.includes(category);
}

/**
 * Render element value (for text-based elements, not arrow/tracker)
 */
export function renderElementValue(
  element: ClockElement,
  currentBG: number,
  bgDelta: number,
  currentTime: Date
): string {
  switch (element.type) {
    case "sg":
      return currentBG.toString();
    case "delta":
      return `${bgDelta > 0 ? "+" : ""}${bgDelta}${element.showUnits !== false ? " mg/dL" : ""}`;
    case "arrow":
      return ""; // Handled separately with Lucide icon
    case "age":
      return "3m ago";
    case "time":
      return formatTime(element.format, currentTime);
    case "iob":
      return "--U";
    case "cob":
      return "--g";
    case "basal":
      return "0.8U/h";
    case "forecast":
      return `${currentBG + 10}`;
    case "summary":
      return "92% in range";
    case "tracker":
      return ""; // Handled separately with icon + time
    case "trackers":
      return "[trackers]";
    case "text":
      return element.text || "Text";
    case "chart":
      return "[chart]";
    default:
      return "";
  }
}
