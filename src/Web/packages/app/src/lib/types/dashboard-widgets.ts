/**
 * Dashboard Widget System
 *
 * Widget icon mappings and helpers.
 * Types must be imported directly from '$lib/api/generated/nocturne-api-client'.
 */

import { WidgetId, type WidgetConfig } from "$lib/api/generated/nocturne-api-client";
import type { TopWidgetId } from "$lib/components/dashboard/top-widget-ids";

import TrendingUp from "@lucide/svelte/icons/trending-up";
import Clock from "@lucide/svelte/icons/clock";
import Wifi from "@lucide/svelte/icons/wifi";
import UtensilsCrossed from "@lucide/svelte/icons/utensils-crossed";
import ListChecks from "@lucide/svelte/icons/list-checks";
import BarChart3 from "@lucide/svelte/icons/chart-column";
import CalendarDays from "@lucide/svelte/icons/calendar-days";
import PieChart from "@lucide/svelte/icons/chart-pie";
import type { Component } from "svelte";

/** Keyed by the registry's ids, so an icon and a loader cannot exist without each other. */
export const WIDGET_ICONS: Record<TopWidgetId, Component> = {
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
 * saying so.
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
