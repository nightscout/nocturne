/**
 * What this build can render. Names, descriptions and placement come from the
 * backend widget catalogue; the picker offers that catalogue's top widgets
 * narrowed to the ids in top-widget-ids.ts, which this map must cover exactly:
 * an id without a loader, or a loader without an id, fails to compile.
 */

import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import type { Component } from "svelte";
import type { TopWidgetId } from "./top-widget-ids";

export {
  DEFAULT_TOP_WIDGETS,
  TOP_WIDGET_IDS,
  isTopWidgetId,
  knownTopWidgets,
  type TopWidgetId,
} from "./top-widget-ids";

type WidgetLoader = () => Promise<{ default: Component }>;

const TOP_WIDGET_LOADERS: Record<TopWidgetId, WidgetLoader> = {
  [WidgetId.BgDelta]: () => import("./widgets/BgDeltaWidget.svelte"),
  [WidgetId.LastUpdated]: () => import("./widgets/LastUpdatedWidget.svelte"),
  [WidgetId.ConnectionStatus]: () =>
    import("./widgets/ConnectionStatusWidget.svelte"),
  [WidgetId.Meals]: () => import("./widgets/MealsWidget.svelte"),
  [WidgetId.Trackers]: () => import("./widgets/TrackersWidget.svelte"),
  [WidgetId.TirChart]: () => import("./widgets/TirChartWidget.svelte"),
  [WidgetId.DailySummary]: () => import("./widgets/DailySummaryWidget.svelte"),
  [WidgetId.Clock]: () => import("./widgets/ClockWidget.svelte"),
  [WidgetId.Tdd]: () => import("./widgets/TddWidget.svelte"),
};

/**
 * What WidgetPlaceholder draws while a widget's component loads: the shape the
 * widget itself renders first (before its own data arrives), so the cell keeps
 * its height when the component replaces the placeholder.
 */
export type TopWidgetPlaceholder =
  | "delta"
  | "stat"
  | "clock"
  | "connection"
  | "empty-state"
  | "caption"
  | "spinner";

const TOP_WIDGET_PLACEHOLDERS: Record<TopWidgetId, TopWidgetPlaceholder> = {
  [WidgetId.BgDelta]: "delta",
  [WidgetId.LastUpdated]: "stat",
  [WidgetId.ConnectionStatus]: "connection",
  [WidgetId.Meals]: "empty-state",
  [WidgetId.Trackers]: "empty-state",
  [WidgetId.TirChart]: "spinner",
  [WidgetId.DailySummary]: "caption",
  [WidgetId.Clock]: "clock",
  [WidgetId.Tdd]: "spinner",
};

export function topWidgetPlaceholder(id: TopWidgetId): TopWidgetPlaceholder {
  return TOP_WIDGET_PLACEHOLDERS[id];
}

const cache = new Map<TopWidgetId, Promise<Component>>();

export function loadTopWidget(id: TopWidgetId): Promise<Component> {
  let loading = cache.get(id);
  if (!loading) {
    loading = TOP_WIDGET_LOADERS[id]().then((m) => m.default);
    cache.set(id, loading);
  }
  return loading;
}
