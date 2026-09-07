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
  LOADABLE_TOP_WIDGETS,
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

const cache = new Map<TopWidgetId, Promise<Component>>();

export function loadTopWidget(id: TopWidgetId): Promise<Component> {
  let loading = cache.get(id);
  if (!loading) {
    loading = TOP_WIDGET_LOADERS[id]().then((m) => m.default);
    cache.set(id, loading);
  }
  return loading;
}
