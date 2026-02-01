/**
 * Dashboard Widget System
 *
 * Widget icon mappings and helpers.
 * Types must be imported directly from '$lib/api/generated/nocturne-api-client'.
 * Widget definitions will eventually come from the backend API.
 */

import {
  WidgetId,
  WidgetPlacement,
  type WidgetConfig,
} from "$lib/api/generated/nocturne-api-client";

import {
  TrendingUp,
  Clock,
  Wifi,
  UtensilsCrossed,
  ListChecks,
  BarChart3,
  CalendarDays,
  LineChart,
  BarChart2,
  Syringe,
  Activity,
  Battery,
  PieChart,
} from "lucide-svelte";
import type { ComponentType } from "svelte";

/**
 * Widget icon mapping - maps widget IDs to their Svelte icon components
 */
export const WIDGET_ICONS: Partial<Record<WidgetId, ComponentType>> = {
  [WidgetId.BgDelta]: TrendingUp,
  [WidgetId.LastUpdated]: Clock,
  [WidgetId.ConnectionStatus]: Wifi,
  [WidgetId.Meals]: UtensilsCrossed,
  [WidgetId.Trackers]: ListChecks,
  [WidgetId.TirChart]: BarChart3,
  [WidgetId.DailySummary]: CalendarDays,
  [WidgetId.GlucoseChart]: LineChart,
  [WidgetId.Statistics]: BarChart2,
  [WidgetId.Predictions]: TrendingUp,
  [WidgetId.DailyStats]: CalendarDays,
  [WidgetId.Treatments]: Syringe,
  [WidgetId.Agp]: Activity,
  [WidgetId.BatteryStatus]: Battery,
  [WidgetId.Clock]: Clock,
  [WidgetId.Tdd]: PieChart,
};

/**
 * Get icon component for a widget
 */
export function getWidgetIcon(id: WidgetId): ComponentType | undefined {
  return WIDGET_ICONS[id];
}

/**
 * Default top widgets (for when no user config exists)
 * BgDelta includes connection status + last updated info
 */
export const DEFAULT_TOP_WIDGETS: WidgetId[] = [
  WidgetId.BgDelta,
  WidgetId.TirChart,
  WidgetId.Tdd,
];

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

/**
 * Get enabled widgets by placement
 */
export function getEnabledWidgetsByPlacement(
  widgets: WidgetConfig[] | undefined,
  placement: WidgetPlacement
): WidgetId[] {
  if (!widgets) {
    // Return defaults for top placement
    if (placement === WidgetPlacement.Top) {
      return DEFAULT_TOP_WIDGETS;
    }
    return [];
  }

  return widgets
    .filter((w) => w.placement === placement && w.enabled)
    .map((w) => w.id) as WidgetId[];
}
