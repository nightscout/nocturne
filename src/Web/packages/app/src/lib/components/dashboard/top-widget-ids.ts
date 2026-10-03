/**
 * The widget ids this build can render in the top grid, without the components
 * behind them. The appearance store persists a default built from this list and
 * is imported by the portal, so this module must not reach any widget component:
 * the loaders live in widget-registry.ts and are typed against these ids.
 */

import { WidgetId } from "../../api/generated/nocturne-api-client";

export const TOP_WIDGET_IDS = [
  WidgetId.BgDelta,
  WidgetId.LastUpdated,
  WidgetId.ConnectionStatus,
  WidgetId.Meals,
  WidgetId.Trackers,
  WidgetId.TirChart,
  WidgetId.DailySummary,
  WidgetId.Clock,
  WidgetId.Tdd,
] as const;

/** A widget id the grid can actually render. */
export type TopWidgetId = (typeof TOP_WIDGET_IDS)[number];

const TOP_WIDGET_ID_SET: ReadonlySet<string> = new Set<string>(TOP_WIDGET_IDS);

export function isTopWidgetId(id: string): id is TopWidgetId {
  return TOP_WIDGET_ID_SET.has(id);
}

/**
 * The top grid a user who has never picked one gets. The backend catalogue
 * deliberately carries no default for a top widget: only this build knows which
 * of them it can render, and the selection is a per-user preference rather than
 * tenant settings.
 */
export const DEFAULT_TOP_WIDGETS: TopWidgetId[] = [
  WidgetId.TirChart,
  WidgetId.Tdd,
  WidgetId.BgDelta,
];

/** The grid shows at most this many; a longer stored list renders its head. */
export const MAX_TOP_WIDGETS = 3;

/**
 * Whether the grid is showing the Current glucose widget (`BgDelta`, kept as the
 * id so stored selections carry over), which then carries the dashboard's
 * reading in place of the header tile.
 */
export function showsCurrentGlucoseWidget(
  ids: readonly string[] | undefined
): boolean {
  return knownTopWidgets(ids)
    .slice(0, MAX_TOP_WIDGETS)
    .includes(WidgetId.BgDelta);
}

/**
 * Selections persist per user, outlive any one release, and arrive from a
 * cookie any tenant subdomain can write, so a stored list can name an id this
 * build has no component for, or no id at all.
 */
export function knownTopWidgets(
  ids: readonly string[] | undefined
): TopWidgetId[] {
  return (ids ?? []).filter(isTopWidgetId);
}
