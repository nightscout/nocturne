/** Central exports for all constants */

import type { ExternalUrls } from "$lib/api";

/**
 * External URLs from the backend - single source of truth
 * These values match UrlConstants.External in the backend.
 * The types are generated from the API via NSwag.
 */
export const EXTERNAL_URLS: ExternalUrls = {
  website: "https://nightscoutfoundation.org/nocturne",
  docsBase: "https://nightscoutfoundation.org/nocturne/docs",
  connectorDocs: {
    dexcom: "https://nightscoutfoundation.org/nocturne/docs/connectors/dexcom",
    libre: "https://nightscoutfoundation.org/nocturne/docs/connectors/libre",
    careLink: "https://nightscoutfoundation.org/nocturne/docs/connectors/carelink",
    nightscout: "https://nightscoutfoundation.org/nocturne/docs/connectors/nightscout",
    glooko: "https://nightscoutfoundation.org/nocturne/docs/connectors/glooko",
  },
};

import type { GlycemicThresholds } from "../api";

export interface CompressionLowConfig {
  enabled: boolean;
  threshold: number;
  duration: number;
  recovery: number;
}

export const DEFAULT_THRESHOLDS: GlycemicThresholds = {
  low: 55,
  targetBottom: 80,
  targetTop: 140,
  tightTargetBottom: 80,
  tightTargetTop: 120,
  high: 180,
  severeLow: 40,
  severeHigh: 250,
};

export const DEFAULT_COMPRESSION_CONFIG: CompressionLowConfig = {
  enabled: true,
  threshold: 40,
  duration: 15,
  recovery: 70,
};

export const DEFAULT_CONFIG = {
  thresholds: DEFAULT_THRESHOLDS,
  sensorType: "GENERIC_5MIN",
  compressionLowConfig: DEFAULT_COMPRESSION_CONFIG,
  includeLoopingMetrics: false,
  units: "mg/dl",
};

export const chartConfig = {
  severeLow: {
    threshold: DEFAULT_THRESHOLDS.severeLow,
    label: "Severe Low",
    color: "var(--glucose-very-low)",
  },
  low: {
    threshold: DEFAULT_THRESHOLDS.low,
    label: "Low",
    color: "var(--glucose-low)",
  },
  target: {
    threshold: DEFAULT_THRESHOLDS.targetBottom,
    label: "Target",
    color: "var(--glucose-in-range)",
  },
  high: {
    threshold: DEFAULT_THRESHOLDS.high,
    label: "High",
    color: "var(--glucose-high)",
  },
  severeHigh: {
    threshold: DEFAULT_THRESHOLDS.severeHigh,
    label: "Severe High",
    color: "var(--glucose-very-high)",
  },
};
