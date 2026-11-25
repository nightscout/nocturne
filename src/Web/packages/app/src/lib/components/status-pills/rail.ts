import {
  RailItemId,
  type DashboardRailPreference,
} from "@nocturne/api-client";
import { isRecord } from "../../utils/type-guards";

/** Every rail item, in the order the default rail shows them. */
export const RAIL_ITEM_IDS: readonly RailItemId[] = Object.values(RailItemId);

/** Known, distinct rail item ids from a stored value, in their stored order. */
export function knownRailItems(raw: unknown): RailItemId[] {
  if (!Array.isArray(raw)) return [];
  const items: RailItemId[] = [];
  for (const value of raw) {
    const id = RAIL_ITEM_IDS.find((known) => known === value);
    if (id && !items.includes(id)) items.push(id);
  }
  return items;
}

/**
 * A stored rail arrangement narrowed to what this build knows, or `undefined` when the value is
 * not one. Unknown ids drop out, so a list of nothing this build knows leaves the default.
 */
export function parseRailPreference(raw: unknown): DashboardRailPreference | undefined {
  if (!isRecord(raw)) return undefined;
  const preference: DashboardRailPreference = {};
  if (Array.isArray(raw.order)) preference.order = knownRailItems(raw.order);
  if (Array.isArray(raw.hidden)) preference.hidden = knownRailItems(raw.hidden);
  return preference;
}

/**
 * Every rail item in the user's order: the ones the preference orders first, then the rest in
 * their default order, so an item this build added still has a place.
 */
export function railOrder(preference: DashboardRailPreference | null | undefined): RailItemId[] {
  const ordered = knownRailItems(preference?.order);
  return [...ordered, ...RAIL_ITEM_IDS.filter((id) => !ordered.includes(id))];
}

/** Whether the user hid an item. */
export function isRailItemHidden(
  preference: DashboardRailPreference | null | undefined,
  id: RailItemId
): boolean {
  return knownRailItems(preference?.hidden).includes(id);
}

/**
 * The items the dashboard rail shows, in order. Trackers show only while the tenant's tracker pill
 * switch is on and the user has not hidden them.
 */
export function effectiveRail(
  preference: DashboardRailPreference | null | undefined,
  trackerPillsEnabled: boolean
): RailItemId[] {
  return railOrder(preference).filter(
    (id) =>
      !isRailItemHidden(preference, id) &&
      (id !== RailItemId.Trackers || trackerPillsEnabled)
  );
}

/** How many rail items a phone shows before the rest; for the default order, COB, basal, IOB and loop. */
export const MOBILE_LEAD_RAIL_ITEMS = 4;

/**
 * A phone's rail: the first items in the user's order lead and the rest wait behind a disclosure.
 * Trackers lead wherever the user placed them while one of their pills warns, so a due change
 * is never behind it.
 */
export function splitMobileRail(
  items: readonly RailItemId[],
  trackersWarn: boolean
): { lead: RailItemId[]; rest: RailItemId[] } {
  const lead = items.filter(
    (id, index) =>
      index < MOBILE_LEAD_RAIL_ITEMS || (id === RailItemId.Trackers && trackersWarn)
  );
  return { lead, rest: items.filter((id) => !lead.includes(id)) };
}
