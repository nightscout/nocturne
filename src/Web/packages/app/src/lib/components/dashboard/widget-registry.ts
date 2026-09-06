/**
 * `WidgetId` also carries ids for the dashboard's main sections, which are not
 * grid widgets, so this — not the enum — is the set the grid renders and the
 * settings picker offers.
 */

import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import type { Component } from "svelte";

type WidgetLoader = () => Promise<{ default: Component }>;

const TOP_WIDGET_LOADERS = {
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
} satisfies Partial<Record<WidgetId, WidgetLoader>>;

/** A widget id the grid can actually render. */
export type TopWidgetId = keyof typeof TOP_WIDGET_LOADERS;

function isTopWidgetId(id: string): id is TopWidgetId {
  return Object.hasOwn(TOP_WIDGET_LOADERS, id);
}

/** Every widget offerable in settings, in the order the picker lists them. */
export const TOP_WIDGET_IDS: TopWidgetId[] =
  Object.keys(TOP_WIDGET_LOADERS).filter(isTopWidgetId);

export const DEFAULT_TOP_WIDGETS: TopWidgetId[] = [
  WidgetId.BgDelta,
  WidgetId.TirChart,
  WidgetId.Tdd,
];

/**
 * Selections persist per user, outlive any one release, and arrive from a
 * cookie any tenant subdomain can write, so a stored list can name an id this
 * build has no component for — or no id at all.
 */
export function knownTopWidgets(
  ids: readonly string[] | undefined
): TopWidgetId[] {
  return (ids ?? []).filter(isTopWidgetId);
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
