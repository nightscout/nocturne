/**
 * Dashboard Widget System
 *
 * Widget icon mappings and helpers.
 * Types must be imported directly from '$lib/api/generated/nocturne-api-client'.
 */

import { WidgetId, type WidgetConfig } from "$lib/api/generated/nocturne-api-client";
import type { TopWidgetId } from "$lib/components/dashboard/top-widget-ids";

import {
  TrendingUp,
  Clock,
  Wifi,
  UtensilsCrossed,
  ListChecks,
  BarChart3,
  CalendarDays,
  PieChart,
} from "lucide-svelte";
import type { ComponentType } from "svelte";

/** Keyed by the registry's ids, so an icon and a loader cannot exist without each other. */
export const WIDGET_ICONS: Record<TopWidgetId, ComponentType> = {
  [WidgetId.BgDelta]: TrendingUp,
  [WidgetId.LastUpdated]: Clock,
  [WidgetId.ConnectionStatus]: Wifi,
  [WidgetId.Meals]: UtensilsCrossed,
  [WidgetId.Trackers]: ListChecks,
  [WidgetId.TirChart]: BarChart3,
  [WidgetId.DailySummary]: CalendarDays,
  [WidgetId.Clock]: Clock,
  [WidgetId.Tdd]: PieChart,
};

/**
 * Whether the dashboard shows a main section. The one place that decides what
 * an id the stored list does not name means, and it means shown: tenant
 * settings arrive after first paint, so the list is undefined for the first
 * render of every visit, and a list written before a section was catalogued
 * names nothing about it. A section is therefore hidden only by a stored row
 * saying so — dropping it from the backend defaults hides it from nobody, and
 * hiding it everywhere means removing its surface here.
 *
 * The top grid is not decided here: it is a per-user preference
 * (`dashboardTopWidgets`), and `widgets` carries no top-placement rows.
 */
export function isMainSectionEnabled(
  widgets: WidgetConfig[] | undefined,
  widgetId: WidgetId
): boolean {
  return widgets?.find((w) => w.id === widgetId)?.enabled ?? true;
}
