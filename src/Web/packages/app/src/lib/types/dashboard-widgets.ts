/**
 * Dashboard Widget System
 *
 * Widget icon mappings and helpers.
 * Types must be imported directly from '$lib/api/generated/nocturne-api-client'.
 */

import { WidgetId, type WidgetConfig } from "$lib/api/generated/nocturne-api-client";
import type { TopWidgetId } from "$lib/components/dashboard/widget-registry";

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
 * Helper to check if a widget is enabled
 */
export function isWidgetEnabled(
  widgets: WidgetConfig[] | undefined,
  widgetId: WidgetId
): boolean {
  if (!widgets) {
    return true; // Default to enabled if no config
  }
  const widget = widgets.find((w) => w.id === widgetId);
  return widget?.enabled ?? true;
}
