/**
 * Clock Builder Factory Functions
 *
 * This module contains factory functions for creating default elements
 * and configurations for the clock face builder.
 */

import { randomUUID } from "$lib/utils";
import type { ClockElement, ClockFaceConfig } from "$lib/api/api-client";
import {
  ELEMENT_INFO,
  DEFAULT_SETTINGS,
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
      color: info.defaultDynamicColor ? "dynamic" : "#ffffff",
      font: "system",
      fontWeight: "medium",
      opacity: 1.0,
    },
  };
  if (info.hasHoursOption) element.hours = 3;
  if (info.hasFormatOption) element.format = "12h";
  if (info.hasMinutesAheadOption) element.minutesAhead = 30;
  if (type === "tracker") {
    element.show = ["name", "remaining"];
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
 * Create a default clock face configuration
 */
export function createDefaultConfig(): ClockFaceConfig {
  return {
    rows: [
      {
        elements: [
          {
            type: "sg",
            size: 40,
            style: {
              color: "dynamic",
              font: "system",
              fontWeight: "medium",
              opacity: 1.0,
            },
          },
          {
            type: "arrow",
            size: 25,
            style: {
              color: "dynamic",
              font: "system",
              fontWeight: "medium",
              opacity: 1.0,
            },
          },
        ],
      },
      {
        elements: [
          {
            type: "delta",
            size: 14,
            showUnits: true,
            style: {
              color: "dynamic",
              font: "system",
              fontWeight: "medium",
              opacity: 1.0,
            },
          },
        ],
      },
      {
        elements: [
          {
            type: "age",
            size: 10,
            style: { font: "system", fontWeight: "medium", opacity: 0.7 },
          },
        ],
      },
    ],
    settings: DEFAULT_SETTINGS,
  };
}

/**
 * Initialize internal config with IDs from a ClockFaceConfig
 */
export function initializeInternalConfig(config?: ClockFaceConfig): InternalConfig {
  const sourceConfig = config ?? createDefaultConfig();
  return {
    rows: (sourceConfig.rows ?? []).map((row) => ({
      _id: randomUUID(),
      elements: (row.elements ?? []).map((el) => ({
        ...el,
        _id: randomUUID(),
      })),
    })),
    settings: sourceConfig.settings ?? { ...DEFAULT_SETTINGS },
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
