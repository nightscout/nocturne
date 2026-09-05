/**
 * Clock Builder Utility Functions
 *
 * This module contains helper functions for styling, color management,
 * and other utilities used by the clock face builder.
 */

import type { ClockElement, ClockSettings, TrackerDefinitionDto } from "$lib/api";
import {
  TEXT_ELEMENT_TYPES,
  elementInfo,
  type InternalElement,
} from "./types";
import { browser } from "$app/environment";

/**
 * Resolve CSS variable to its computed value
 */
function resolveCssVar(name: string): string {
  if (!browser) return "#000000"; // fallback for SSR
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

const DEFAULT_ELEMENT_COLOR = "#ffffff";

/**
 * Get BG color based on glucose value
 */
export function getBgColor(bg: number): string {
  if (bg < 70) return resolveCssVar("--glucose-very-low");
  if (bg < 80) return resolveCssVar("--glucose-low");
  if (bg > 250) return resolveCssVar("--glucose-very-high");
  if (bg > 180) return resolveCssVar("--glucose-high");
  return resolveCssVar("--glucose-in-range");
}

/**
 * Background of a whole clock face. A face set to colour itself by glucose
 * takes `fallback` when there is no reading, rather than painting the screen
 * with the colour of a value nothing reported.
 */
export function clockBackgroundStyle(
  settings: ClockSettings | undefined,
  currentBG: number | null,
  fallback: string
): string {
  if (settings?.backgroundImage) {
    return `background-image: url(${settings.backgroundImage}); background-size: cover; background-position: center;`;
  }
  if (settings?.bgColor && currentBG !== null) {
    return `background-color: ${getBgColor(currentBG)};`;
  }
  return `background-color: ${fallback};`;
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
 * Get element text color from style. A null `currentBG` has no glucose colour:
 * a dynamic element falls back to the static default rather than painting the
 * absence of a reading as a severe low.
 */
export function getElementColor(
  element: ClockElement,
  currentBG: number | null
): string {
  const color = element.style?.color;
  if (color === "dynamic") {
    return currentBG === null ? DEFAULT_ELEMENT_COLOR : getBgColor(currentBG);
  }
  return color || DEFAULT_ELEMENT_COLOR;
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
export function buildStyleString(
  element: ClockElement,
  currentBG: number | null
): string {
  const style = element.style;
  const parts: string[] = [];

  // Font size from element.size
  const size = element.size || elementInfo(element.type)?.defaultSize || 20;
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
 * Check if element is a text-based element (uses unified text rendering)
 */
export function isTextElement(type: string): boolean {
  return TEXT_ELEMENT_TYPES.includes(type);
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
