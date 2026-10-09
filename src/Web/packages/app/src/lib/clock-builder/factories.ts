/**
 * Clock Builder Factory Functions
 *
 * This module contains factory functions for creating default elements
 * and configurations for the clock face builder.
 */

import { randomUUID } from "$lib/utils";
import type { ClockElement, ClockFaceConfig } from "$lib/api";
import { DEFAULT_ELEMENT_COLOR } from "./utils";
import {
  ELEMENT_INFO,
  type ClockElementType,
  type InternalElement,
  type InternalRow,
  type InternalConfig,
} from "./types";

/**
 * Create a default element of the given type
 */
export function createDefaultElement(type: ClockElementType): ClockElement {
  const info = ELEMENT_INFO[type];
  const element: ClockElement = {
    type,
    size: info.defaultSize,
    style: {
      color: info.defaultDynamicColor ? "dynamic" : DEFAULT_ELEMENT_COLOR,
      font: "system",
      fontWeight: "medium",
      opacity: 1.0,
    },
  };
  if (info.hasHoursOption) element.hours = 3;
  if (info.hasMinutesAheadOption) element.minutesAhead = 30;
  if (type === "tracker") {
    element.show = ["name"];
    element.visibilityThreshold = "always";
  }
  if (type === "trackers") {
    element.visibilityThreshold = "always";
    element.categories = [];
  }
  if (type === "text") {
    element.text = "Label";
  }
  if (type === "age" || type === "time") {
    element.style = { ...element.style, opacity: 0.7 };
  }
  if (type === "chart") {
    element.width = 400;
    element.height = 200;
    element.hours = 3;
    element.chartConfig = {
      showIob: false,
      showCob: false,
      showBasal: false,
      showBolus: true,
      showCarbs: true,
      showDeviceEvents: false,
      showAlarms: false,
      showTrackers: false,
      showPredictions: false,
      lockToggles: true,
      showLegend: false,
      asBackground: false,
    };
  }
  return element;
}

/**
 * Initialize internal config with IDs from a ClockFaceConfig
 */
export function initializeInternalConfig(
  sourceConfig: ClockFaceConfig
): InternalConfig {
  return {
    rows: (sourceConfig.rows ?? []).map((row) => ({
      _id: randomUUID(),
      elements: (row.elements ?? []).map((el) => ({
        ...el,
        _id: randomUUID(),
      })),
    })),
    settings: sourceConfig.settings ?? {},
  };
}

/**
 * Convert internal config back to API config (strips _id fields)
 */
export function toApiConfig(config: InternalConfig): ClockFaceConfig {
  return {
    rows: config.rows.map((row) => ({
      elements: row.elements.map(({ _id, ...rest }) => rest),
    })),
    settings: config.settings,
  };
}

/**
 * Create an internal element from a ClockElementType
 */
export function createInternalElement(type: ClockElementType): InternalElement {
  const element = createDefaultElement(type);
  return { ...element, _id: randomUUID() };
}

/**
 * Create an empty internal row
 */
export function createInternalRow(elements: InternalElement[] = []): InternalRow {
  return {
    _id: randomUUID(),
    elements,
  };
}
