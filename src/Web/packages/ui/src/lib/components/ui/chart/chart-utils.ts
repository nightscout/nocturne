import type { Tooltip } from "layerchart";
import { getContext, setContext, type Component, type Snippet } from "svelte";

export const THEMES = { light: "", dark: ".dark" } as const;

export type ChartConfig = {
  [k in string]: {
    label?: string;
    icon?: Component;
  } & (
    | { color?: string; theme?: never }
    | { color?: never; theme: Record<keyof typeof THEMES, string> }
  );
};

export type ExtractSnippetParams<T> = T extends Snippet<[infer P]> ? P : never;

export type TooltipPayload = Tooltip.TooltipSeries;

/** `source[key]` when `source` is an object holding a string there. */
function stringAt(source: unknown, key: string): string | undefined {
  if (typeof source !== "object" || source === null) return undefined;
  const value: unknown = Reflect.get(source, key);
  return typeof value === "string" ? value : undefined;
}

/** The colour a config entry takes under `theme`, one of the keys of {@link THEMES}. */
export function themeColor(itemConfig: ChartConfig[string], theme: string): string | undefined {
  const themed = itemConfig.theme ? new Map(Object.entries(itemConfig.theme)).get(theme) : undefined;
  return themed || itemConfig.color;
}

// Helper to extract item config from a payload.
export function getPayloadConfigFromPayload(
  config: ChartConfig,
  payload: TooltipPayload,
  key: string,
  data?: unknown
) {
  if (typeof payload !== "object" || payload === null) return undefined;

  const payloadConfig = "config" in payload ? payload.config : undefined;

  const configLabelKey =
    payload.key === key || payload.label === key
      ? key
      : (stringAt(payload, key) ?? stringAt(payloadConfig, key) ?? stringAt(data, key) ?? key);

  return configLabelKey in config ? config[configLabelKey] : config[key];
}

type ChartContextValue = {
  config: ChartConfig;
};

const chartContextKey = Symbol("chart-context");

export function setChartContext(value: ChartContextValue) {
  return setContext(chartContextKey, value);
}

export function useChart() {
  return getContext<ChartContextValue>(chartContextKey);
}
